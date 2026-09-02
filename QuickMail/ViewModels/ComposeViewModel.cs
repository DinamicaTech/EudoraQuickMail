using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class ComposeViewModel : ObservableObject, IDisposable
{
    private const long AttachmentWarningThresholdBytes = 25_000_000;
    /// <summary>Raised after a local Draft or Scheduled folder may have been created.</summary>
    public event Action<Guid>? LocalFolderChanged;
    /// <summary>Raised after SMTP accepted a message and its Sent copy has been handled.</summary>
    public event Action<Guid>? SentMailChanged;
    private readonly ISendMailService _smtp;
    private readonly IAccountService _accountService;
    private readonly ICredentialService _credentials;
    private readonly IMailService _imap;
    private readonly ILocalStoreService? _localStore;
    private readonly ITemplateService _templateService;
    private readonly IMarkdownService _markdown;

    [ObservableProperty] private string _to = string.Empty;
    [ObservableProperty] private string _cc = string.Empty;
    [ObservableProperty] private string _bcc = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    private string _subject = string.Empty;

    /// <summary>What kind of composition this is; drives the window title prefix.</summary>
    public ComposeKind ComposeKind { get; private set; } = ComposeKind.NewMessage;

    /// <summary>
    /// Dynamic window title: "{subject or kind} - {mode} - QuickMail".
    /// The subject leads so the taskbar and Alt+Tab identify the message; the
    /// compose mode follows so the editing format is always visible.
    /// </summary>
    public string WindowTitle
    {
        get
        {
            var kindLabel = ComposeKind switch
            {
                ComposeKind.Reply        => "Reply",
                ComposeKind.ReplyAll     => "Reply All",
                ComposeKind.Forward      => "Forward",
                ComposeKind.EditDraft    => "Draft",
                ComposeKind.NewDraft     => "Draft",
                ComposeKind.EditScheduled => "Scheduled Message",
                ComposeKind.EditTemplate => "Edit Template",
                ComposeKind.EditStoredMessage => "Edit Message",
                _                        => "New Message",
            };
            var lead = string.IsNullOrWhiteSpace(Subject) ? kindLabel : Subject.Trim();
            var mode = CurrentMode switch
            {
                ComposeMode.Markdown => "Markdown",
                ComposeMode.Html     => "HTML",
                _                    => "Plain Text",
            };
            return $"{lead} - {mode} - QuickMail";
        }
    }
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeDisplay))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(IsHtmlMode))]
    [NotifyPropertyChangedFor(nameof(IsMarkdownMode))]
    [NotifyPropertyChangedFor(nameof(IsPreviewAvailable))]
    [NotifyPropertyChangedFor(nameof(IsFormattingAvailable))]
    [NotifyPropertyChangedFor(nameof(IsSpellNavAvailable))]
    private ComposeMode _currentMode = ComposeMode.PlainText;

    /// <summary>True in HTML mode — some formatting (underline) is HTML-only.</summary>
    public bool IsHtmlMode => CurrentMode == ComposeMode.Html;

    /// <summary>True in Markdown mode — drives preview availability.</summary>
    public bool IsMarkdownMode => CurrentMode == ComposeMode.Markdown;

    /// <summary>True in Markdown or HTML mode — the preview window is available in both.</summary>
    public bool IsPreviewAvailable => CurrentMode == ComposeMode.Markdown || CurrentMode == ComposeMode.Html;

    /// <summary>
    /// Formatting commands work in both rich modes: HTML applies real formatting,
    /// Markdown inserts the equivalent syntax. Only Plain Text has none.
    /// </summary>
    public bool IsFormattingAvailable => CurrentMode != ComposeMode.PlainText;

#pragma warning disable CA1822 // [NotifyPropertyChangedFor] raises PropertyChanged for this property on the instance, so it must be an instance member
    public bool IsSpellNavAvailable => true;
#pragma warning restore CA1822
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _scheduledForLocal = DateTime.Now.AddMinutes(10).ToString("g");

    /// <summary>
    /// Which announcement category the View reads the accompanying <see cref="StatusText"/> under.
    ///
    /// Status is right for background progress ("Sending…", "Saving draft…") and wrong for the
    /// outcome of a command the user just invoked. Announcing "Send failed" as Status meant anyone
    /// who had turned background-progress announcements off pressed Send, watched the button come
    /// back enabled, and was told nothing at all — the report in #396.
    ///
    /// One-shot: it returns to Status after every raise, so a message assigned through neither
    /// helper cannot inherit a latched Result and interrupt. Same contract as
    /// <see cref="MainViewModel.StatusAnnouncementCategory"/>.
    /// </summary>
    public AnnouncementCategory StatusCategory { get; private set; } = AnnouncementCategory.Status;

    /// <summary>
    /// Sets <see cref="StatusText"/> as the outcome of a user action, so it is announced as a
    /// Result and survives the background-progress preference. Public because the compose window
    /// reports a few outcomes of its own (address checks, address-book adds) and must classify
    /// them the same way rather than announcing alongside the binding.
    ///
    /// The reset is safe because StatusText's PropertyChanged fires synchronously: the View has
    /// already read the category by the time this returns.
    /// </summary>
    public void SetStatusOutcome(string text) => SetStatus(text, AnnouncementCategory.Result);

    /// <summary>Sets <see cref="StatusText"/> as background progress.</summary>
    private void SetProgress(string text) => SetStatus(text, AnnouncementCategory.Status);

    private void SetStatus(string text, AnnouncementCategory category)
    {
        StatusCategory = category;
        // Cleared first so an identical message repeats. StatusText is an [ObservableProperty] with
        // an equality check, so pressing Send twice with the same empty recipient box would raise no
        // notification the second time — the user presses the button and hears nothing, which is the
        // symptom this change exists to remove. The empty value is never announced; the View skips
        // empty status text.
        StatusText = string.Empty;
        StatusText = text;
        StatusCategory = AnnouncementCategory.Status;
    }

    [ObservableProperty] private bool _isBusy = false;
    [ObservableProperty] private ObservableCollection<AccountModel> _senderAccounts = [];
    [ObservableProperty] private AccountModel? _senderAccount;
    [ObservableProperty] private ObservableCollection<AttachmentModel> _attachments = [];

    private string? _inReplyToMessageId;
    private Guid? _replySourceAccountId;
    private string? _replySourceFolderName;
    private string? _replySourceMessageId;
    private string? _draftMessageId;
    private string? _draftFolderName;
    private Guid? _scheduledId;
    private string? _scheduledLocalMessageId;
    private DateTimeOffset? _scheduledAt;
    private Guid _storedMessageAccountId;
    private string? _storedMessageId;
    private string? _storedFolderName;
    private bool _isDirty;
    private bool _isSent;
    private ComposeMode _seededMode = ComposeMode.PlainText;
    private string? _seededHtmlBody;
    private string _appliedPlainSignatureBlock = string.Empty;
    private bool _seedComplete;

    public bool IsDirty => _isDirty;
    public bool IsSent  => _isSent;
    public bool IsEditingStoredMessage => ComposeKind == ComposeKind.EditStoredMessage;

    public bool IsEditingDraft(Guid accountId, string folderName, string messageId) =>
        SenderAccount?.Id == accountId
        && !string.IsNullOrEmpty(_draftMessageId)
        && string.Equals(_draftMessageId, messageId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_draftFolderName, folderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The compose mode stored in the draft when it was last saved; PlainText for new composes.</summary>
    public ComposeMode SeededMode => _seededMode;

    public event Action? CloseRequested;
    public event Action<Guid, string, string>? OriginalMessageReplied;
    public Func<ComposeModel, Task>? SaveStoredMessageRequested { get; set; }

    /// <summary>
    /// Set by the View to show a Yes/No confirmation dialog.
    /// Parameters: message, title. Returns true when the user confirms.
    /// Mirrors the pattern on MainViewModel — see CLAUDE.md MVVM Rules.
    /// </summary>
    public Func<string, string, bool>? ConfirmationRequested { get; set; }
    public event Action<string, string>? ErrorDialogRequested;
    public event Action<string, string>? WarningDialogRequested;

    /// <summary>
    /// The IAccountService this compose window was built with. Exposed so the address book
    /// opened from here (Ctrl+Shift+B) can name accounts in its account filter — without it
    /// every synced account reads as an indistinguishable "Synced contact".
    /// </summary>
    public IAccountService AccountService => _accountService;

    public ComposeViewModel(ISendMailService smtp, IAccountService accountService, ICredentialService credentials,
        IMailService imap, ITemplateService templateService, IMarkdownService? markdown = null,
        ILocalStoreService? localStore = null)
    {
        _smtp = smtp;
        _accountService = accountService;
        _credentials = credentials;
        _imap = imap;
        _templateService = templateService;
        _markdown = markdown ?? new MarkdownService();
        _localStore = localStore;
        _attachments.CollectionChanged += (_, _) =>
        {
            _isDirty = true;
            OnPropertyChanged(nameof(AttachmentSummaryText));
        };
    }

    // Dirty-marking partial methods — fired by the [ObservableProperty] source generator
    partial void OnToChanged(string value)      => _isDirty = true;
    partial void OnCcChanged(string value)      => _isDirty = true;
    partial void OnBccChanged(string value)     => _isDirty = true;
    partial void OnSubjectChanged(string value) => _isDirty = true;
    partial void OnBodyChanged(string value)    => _isDirty = true;
    [ObservableProperty] private string _spellLanguage = string.Empty;
    partial void OnSpellLanguageChanged(string value) => _isDirty = true;
    partial void OnSenderAccountChanged(AccountModel? value)
    {
        if (!_seedComplete) return;
        ReplaceSignature(value);
    }

    public string TabTitle => string.IsNullOrWhiteSpace(Subject)
        ? ComposeKind switch
        {
            ComposeKind.Reply => "Reply",
            ComposeKind.ReplyAll => "Reply All",
            ComposeKind.Forward => "Forward",
            ComposeKind.EditDraft or ComposeKind.NewDraft => "Draft",
            ComposeKind.EditScheduled => "Scheduled Message",
            ComposeKind.EditTemplate => "Edit Template",
            ComposeKind.EditStoredMessage => "Edit Message",
            _ => "New Message",
        }
        : Subject.Trim();

    public void Seed(ComposeModel model)
    {
        _inReplyToMessageId = model.InReplyToMessageId;
        _replySourceAccountId = model.ReplySourceAccountId;
        _replySourceFolderName = model.ReplySourceFolderName;
        _replySourceMessageId = model.ReplySourceMessageId;
        _draftMessageId     = model.DraftMessageId;
        _draftFolderName    = model.DraftFolderName;
        _scheduledId = model.ScheduledId;
        _scheduledLocalMessageId = model.ScheduledLocalMessageId;
        _scheduledAt = model.ScheduledAt;
        _storedMessageAccountId = model.AccountId;
        _storedMessageId = model.StoredMessageId;
        _storedFolderName = model.StoredFolderName;
        ComposeKind         = model.Kind;
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(TabTitle));

        To      = model.To;
        Cc      = model.Cc;
        Bcc     = model.Bcc;
        Subject = model.Subject;
        Body    = model.Body;
        SpellLanguage = model.SpellLanguage ?? string.Empty;

        Attachments.Clear();
        foreach (var att in model.Attachments)
            Attachments.Add(att);

        // Remember the original mode so the View can restore it after wiring up event handlers.
        _seededMode    = model.Mode;
        _seededHtmlBody = model.Mode == ComposeMode.Html ? model.HtmlBody : null;

        // Loading existing data (reply, forward, or re-opened draft) is not itself a dirty edit
        _isDirty = false;

        var accounts = _accountService.LoadAccounts();
        SenderAccounts = new ObservableCollection<AccountModel>(accounts);
        SenderAccount = SenderAccounts.FirstOrDefault(a => a.Id == model.AccountId)
                        ?? SenderAccounts.FirstOrDefault(a => a.IsDefault)
                        ?? SenderAccounts.FirstOrDefault();

        // Auto-append signature if this is a new compose (not a draft re-open) and the
        // account has a signature configured. Drafts already have the signature in the body.
        if (model.DraftMessageId == null && model.ScheduledId == null
            && model.Kind != ComposeKind.EditStoredMessage
            && model.Kind != ComposeKind.EditScheduled
            && SenderAccount != null && !string.IsNullOrWhiteSpace(SenderAccount.Signature))
        {
            var sig = SenderAccount.Signature;
            var bodyBeforeSignature = Body;
            var plainSignature = SenderAccount.SignatureIsHtml
                ? HtmlStripper.ToPlainText(sig)
                : sig;
            // Add separator if body already has content (reply/forward)
            _appliedPlainSignatureBlock = BuildPlainSignatureBlock(bodyBeforeSignature, plainSignature);
            Body += _appliedPlainSignatureBlock;

            // New messages are seeded as Plain Text and switched to the user's default mode
            // only after the editor is ready. Prepare the HTML signature regardless of the
            // seeded mode so an image-only signature is not flattened to an empty string.
            var html = _seededHtmlBody ?? _markdown.PlainTextToHtml(bodyBeforeSignature);
            _seededHtmlBody = AppendBeforeClosingBody(html,
                "<p><br></p>" + BuildHtmlSignatureBlock(SenderAccount, bodyBeforeSignature));
            _isDirty = false; // signature insertion is not a user edit
        }

        _seedComplete = true;
    }

    private void ReplaceSignature(AccountModel? account)
    {
        var wasDirty = _isDirty;
        var plainBody = Body;
        if (!string.IsNullOrEmpty(_appliedPlainSignatureBlock)
            && plainBody.EndsWith(_appliedPlainSignatureBlock, StringComparison.Ordinal))
            plainBody = plainBody[..^_appliedPlainSignatureBlock.Length];

        var plainSignature = account == null || string.IsNullOrWhiteSpace(account.Signature)
            ? string.Empty
            : account.SignatureIsHtml
                ? HtmlStripper.ToPlainText(account.Signature)
                : account.Signature;
        _appliedPlainSignatureBlock = string.IsNullOrWhiteSpace(plainSignature)
            ? string.Empty
            : BuildPlainSignatureBlock(plainBody, plainSignature);
        Body = plainBody + _appliedPlainSignatureBlock;

        if (CurrentMode == ComposeMode.Html)
        {
            var signature = account != null && !string.IsNullOrWhiteSpace(account.Signature)
                ? BuildHtmlSignatureBlock(account, RichBodyProvider?.Invoke().PlainText ?? string.Empty)
                : null;
            ReplaceHtmlSignatureRequested?.Invoke(signature);
        }

        _isDirty = wasDirty;
    }

    private static string BuildPlainSignatureBlock(string body, string signature)
    {
        var prefix = string.IsNullOrWhiteSpace(body)
            ? string.Empty
            : body.EndsWith('\n') ? "\n-- \n" : "\n\n-- \n";
        return prefix + signature;
    }

    private static string BuildHtmlSignatureBlock(AccountModel account, string body)
    {
        var signatureFragment = account.SignatureIsHtml
            ? ExtractHtmlBodyFragment(account.Signature)
            : WebUtility.HtmlEncode(account.Signature)
                .Replace("\r\n", "<br>", StringComparison.Ordinal)
                .Replace("\n", "<br>", StringComparison.Ordinal);
        var separator = string.IsNullOrWhiteSpace(body) ? string.Empty : "<br>-- <br>";
        return $"<div class=\"quickmail-signature\" data-quickmail-signature=\"true\">{separator}{signatureFragment}</div>";
    }

    private static string AppendBeforeClosingBody(string html, string fragment)
    {
        var index = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? html.Insert(index, fragment) : html + fragment;
    }

    private static string ExtractHtmlBodyFragment(string html)
    {
        var body = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (body < 0) return html;
        var contentStart = html.IndexOf('>', body);
        if (contentStart < 0) return html;
        var contentEnd = html.IndexOf("</body>", contentStart + 1, StringComparison.OrdinalIgnoreCase);
        return contentEnd < 0
            ? html[(contentStart + 1)..]
            : html[(contentStart + 1)..contentEnd];
    }

    [RelayCommand]
    private async Task SaveDraftAsync()
    {
        var account = SenderAccount;
        if (account == null)
        {
            SetStatusOutcome("Please select a sender account.");
            return;
        }

        IsBusy = true;
        SetProgress("Saving draft…");
        try
        {
            await SaveDraftCoreAsync(account);
            LocalFolderChanged?.Invoke(account.Id);
            SetStatusOutcome("Draft saved.");
            CloseRequested?.Invoke();
        }
        catch (DraftFolderMissingException)
        {
            SetStatusOutcome("No Drafts folder found on this account.");
        }
        catch (Exception ex)
        {
            SetStatusOutcome($"Save draft failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Uploads the current compose state as a draft, replacing any previous draft.</summary>
    private async Task SaveDraftCoreAsync(AccountModel account, CancellationToken externalCt = default)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, externalCt);

        _draftFolderName ??= await _imap.FindDraftsFolderNameAsync(account.Id, combined.Token);
        if (_draftFolderName == null)
            throw new DraftFolderMissingException();

        var compose = BuildComposeModel(account.Id);
        _draftMessageId = await _imap.AppendDraftAsync(account.Id, compose, _draftMessageId, combined.Token);
        _isDirty = false;
    }

    private sealed class DraftFolderMissingException : Exception
    {
        public DraftFolderMissingException() { }
        public DraftFolderMissingException(string message) : base(message) { }
        public DraftFolderMissingException(string message, Exception innerException) : base(message, innerException) { }
    }

    // ── Auto-save ──────────────────────────────────────────────────────────────

    private readonly CancellationTokenSource _autoSaveCts = new();

    /// <summary>Cancels any in-flight autosave. Called by the window on closing.</summary>
    public void CancelAutoSave() => _autoSaveCts.Cancel();

    public void Dispose()
    {
        _autoSaveCts.Cancel();
        _autoSaveCts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Visual status-row text, e.g. "Auto-saved 3:42 PM". Never announced on success.</summary>
    [ObservableProperty] private string _autoSaveText = string.Empty;

    /// <summary>True once a failed auto-save has been announced; reset by the next success.</summary>
    private bool _autoSaveFailureAnnounced;

    /// <summary>
    /// Raised when an auto-save fails for the first time since the last success,
    /// so the View can announce it once instead of nagging every interval.
    /// </summary>
    public event Action<string>? AutoSaveFailed;

    /// <summary>
    /// Periodic background draft save. Quiet by design: success only updates
    /// <see cref="AutoSaveText"/> (visual status), and failures are announced once.
    /// Skips templates (saving a template edit as a mail draft would be wrong),
    /// untouched composes, and composes with no content worth keeping.
    /// </summary>
    public async Task AutoSaveAsync()
    {
        if (!_isDirty || _isSent || IsBusy) return;
        if (ComposeKind is ComposeKind.EditTemplate or ComposeKind.EditStoredMessage) return;
        var account = SenderAccount;
        if (account == null) return;
        if (!HasAutoSavableContent()) return;

        IsBusy = true;
        try
        {
            await SaveDraftCoreAsync(account, _autoSaveCts.Token);
            LocalFolderChanged?.Invoke(account.Id);
            AutoSaveText = $"Auto-saved {DateTime.Now:t}";
            _autoSaveFailureAnnounced = false;
        }
        catch (Exception ex)
        {
            LogService.Log("AutoSaveAsync: draft auto-save failed", ex);
            AutoSaveText = "Auto-save failed";
            if (!_autoSaveFailureAnnounced)
            {
                _autoSaveFailureAnnounced = true;
                AutoSaveFailed?.Invoke("Auto-save failed. Your draft is not saved to the server.");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Something worth keeping: any recipient, subject, body text, or attachment.</summary>
    private bool HasAutoSavableContent()
    {
        if (!string.IsNullOrWhiteSpace(To) || !string.IsNullOrWhiteSpace(Cc) || !string.IsNullOrWhiteSpace(Bcc))
            return true;
        if (!string.IsNullOrWhiteSpace(Subject)) return true;
        if (Attachments.Count > 0) return true;
        if (CurrentMode == ComposeMode.Html)
            return !(RichBodyProvider?.Invoke() ?? RichBodySnapshot.Empty).IsEmpty;
        return !string.IsNullOrWhiteSpace(Body);
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(To))
        {
            SetStatusOutcome("Please enter at least one recipient.");
            return;
        }

        var account = SenderAccount;
        if (account == null)
        {
            SetStatusOutcome("Please select a sender account.");
            return;
        }

        WarnIfAttachmentsExceedRecommendedSize();

        // The From header is built from this address, so an account whose "email address" is not one
        // — a login name typed into the field before it was validated at save time — produces
        // MAIL FROM:<name>, which the server rejects with a message about the recipient or the
        // sender that explains nothing. Say what is actually wrong, and where to fix it. (#396)
        if (!EmailAddressValidator.IsValid(account.Username))
        {
            SetStatusOutcome($"\"{account.Username}\" is not a valid email address, so this message has no " +
                       "valid sender. Fix the email address for this account in Manage Accounts.");
            return;
        }

        var password = account.BackendKind == BackendKind.Pop3Smtp
            ? null : _credentials.GetPassword(account.Id);
        if (string.IsNullOrEmpty(password) && account.AuthType == Models.AuthType.Password
            && account.BackendKind != BackendKind.Pop3Smtp)
        {
            SetStatusOutcome("No password stored for this account.");
            ErrorDialogRequested?.Invoke("Message could not be sent",
                MailOperationError.Describe("Send email (SMTP)", account,
                    new InvalidOperationException("No password is stored for this account.")));
            return;
        }

        IsBusy = true;
        SetProgress("Sending…");
        try
        {
            var compose = BuildComposeModel(account.Id);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _smtp.SendAsync(compose, account, password, cts.Token);
            await MarkOriginalMessageRepliedBestEffortAsync(compose);
            if (_scheduledId is { } scheduledId && System.Windows.Application.Current is App { ScheduledSender: { } scheduler })
                await scheduler.RemoveAsync(scheduledId, deleteLocalCopy: true);
            SetStatusOutcome("Message sent.");
            _isSent = true;

            // Append to Sent folder (best-effort — fire and forget so it doesn't block the UI),
            // then tell the main mailbox to synchronize/refresh its canonical Out aggregate.
            // Previously the remote append succeeded but Sent was excluded from every cache sweep,
            // leaving Gmail with hundreds of server Sent messages and zero local rows.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var sentCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _imap.AppendToSentAsync(account.Id, compose, sentCts.Token);
                }
                catch (Exception ex)
                {
                    LogService.Log("SendAsync: failed to append to Sent folder", ex);
                }
                finally
                {
                    SentMailChanged?.Invoke(account.Id);
                }
            });

            // Delete the draft from the server (if one was saved)
            if (!string.IsNullOrEmpty(_draftMessageId) && _draftFolderName != null)
            {
                try
                {
                    using var delCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await _imap.MoveToTrashAsync(account.Id, _draftFolderName, _draftMessageId, delCts.Token);
                }
                catch (Exception ex)
                {
                    LogService.Log("SendAsync: failed to delete draft after send", ex);
                }
            }

            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            SetStatusOutcome($"Send failed: {ex.Message}");
            ErrorDialogRequested?.Invoke("Message could not be sent",
                MailOperationError.Describe("Send email (SMTP)", account, ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScheduleSendAsync()
    {
        if (string.IsNullOrWhiteSpace(To)) { SetStatusOutcome("Please enter at least one recipient."); return; }
        if (SenderAccount is not { } account) { SetStatusOutcome("Please select a sender account."); return; }
        WarnIfAttachmentsExceedRecommendedSize();
        var requested = PromptScheduleTimeRequested?.Invoke(_scheduledAt?.LocalDateTime ?? DateTime.Now.AddMinutes(10));
        if (requested is not { } local) return;
        if (local <= DateTime.Now) { SetStatusOutcome("Enter a future local date and time for scheduled sending."); return; }
        if (System.Windows.Application.Current is not App { ScheduledSender: { } scheduler })
        { SetStatusOutcome("Scheduled sending is unavailable."); return; }
        var compose = BuildComposeModel(account.Id);
        if (_scheduledId is { } scheduledId)
            await scheduler.ReplaceAsync(scheduledId, compose, new DateTimeOffset(local));
        else
            await scheduler.ScheduleAsync(compose, new DateTimeOffset(local));
        LocalFolderChanged?.Invoke(account.Id);
        _isSent = true;
        SetStatusOutcome($"Message scheduled for {local:g}.");
        CloseRequested?.Invoke();
    }

    private async Task MarkOriginalMessageRepliedBestEffortAsync(ComposeModel compose)
    {
        if (_localStore is null || compose.ReplySourceAccountId is not { } sourceAccountId
            || string.IsNullOrWhiteSpace(compose.ReplySourceFolderName)
            || string.IsNullOrWhiteSpace(compose.ReplySourceMessageId))
            return;
        try
        {
            await _localStore.UpdateIsRepliedAsync(sourceAccountId, compose.ReplySourceFolderName,
                compose.ReplySourceMessageId, compose.InReplyToMessageId);
            OriginalMessageReplied?.Invoke(sourceAccountId, compose.ReplySourceFolderName,
                compose.ReplySourceMessageId);
        }
        catch (Exception ex)
        {
            // SMTP already accepted the message. Never invite a duplicate retry merely because
            // persisting this local UI marker failed.
            LogService.Log("SendAsync: message sent but original could not be marked replied", ex);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    [RelayCommand]
    private async Task SaveStoredMessageAsync()
    {
        if (!IsEditingStoredMessage || string.IsNullOrWhiteSpace(_storedMessageId)
            || string.IsNullOrWhiteSpace(_storedFolderName) || SaveStoredMessageRequested is null)
        {
            SetStatusOutcome("This message cannot be updated in the local store.");
            return;
        }

        IsBusy = true;
        SetProgress("Saving message…");
        try
        {
            var model = BuildComposeModel(_storedMessageAccountId);
            model.StoredMessageId = _storedMessageId;
            model.StoredFolderName = _storedFolderName;
            await SaveStoredMessageRequested(model);
            _isDirty = false;
            SetStatusOutcome("Message saved.");
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            SetStatusOutcome($"Save failed: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    // ── Compose modes ──────────────────────────────────────────────────────────

    /// <summary>Status-bar label for the active mode, e.g. "Mode: Markdown".</summary>
    public string ModeDisplay => "Mode: " + CurrentMode switch
    {
        ComposeMode.Markdown => "Markdown",
        ComposeMode.Html     => "HTML",
        _                    => "Plain Text",
    };

    /// <summary>
    /// Set by the View. Returns the rich editor's current content serialized as
    /// HTML, Markdown, and plain text. Null until the View wires it.
    /// </summary>
    public Func<RichBodySnapshot>? RichBodyProvider { get; set; }

    /// <summary>
    /// Raised when content must flow into the rich editor (entering HTML mode).
    /// The View converts the HTML fragment into the editor document.
    /// </summary>
    public event Action<string>? LoadHtmlIntoEditorRequested;

    /// <summary>Raised when changing From must replace the signature in the live HTML editor.</summary>
    public event Action<string?>? ReplaceHtmlSignatureRequested;

    /// <summary>
    /// Loads the rich body prepared during Seed when the window was constructed directly in HTML
    /// mode. In that path SetMode(Html) is intentionally a no-op, so the View calls this once.
    /// </summary>
    public bool LoadSeededHtmlBody()
    {
        if (string.IsNullOrWhiteSpace(_seededHtmlBody)) return false;
        var html = _seededHtmlBody;
        _seededHtmlBody = null;
        LoadHtmlIntoEditorRequested?.Invoke(html);
        return true;
    }

    /// <summary>
    /// Raised to insert plain text (e.g. a template) at the rich editor's caret
    /// while in HTML mode.
    /// </summary>
    public event Action<string>? InsertTextIntoEditorRequested;

    /// <summary>
    /// Switches the editing mode, converting the body content. Switching from a
    /// rich mode to Plain Text asks for confirmation because formatting is lost.
    /// Returns true when the switch happened.
    /// </summary>
    public bool SetMode(ComposeMode newMode)
    {
        if (newMode == CurrentMode) return false;

        // Downgrading to plain text discards formatting — confirm first.
        // An unwired ConfirmationRequested (tests) counts as confirmed so the
        // switch is never silently impossible.
        if (newMode == ComposeMode.PlainText && HasFormattingWorthConfirming())
        {
            var confirmed = ConfirmationRequested?.Invoke(
                "Formatting will be lost when switching to Plain Text. Continue?",
                "Switch to Plain Text") ?? true;
            if (!confirmed) return false;
        }

        switch (CurrentMode, newMode)
        {
            case (ComposeMode.PlainText, ComposeMode.Markdown):
                break; // plain text is valid Markdown source — pass through as-is

            case (ComposeMode.PlainText, ComposeMode.Html):
                var htmlToLoad = _seededHtmlBody ?? _markdown.PlainTextToHtml(Body);
                _seededHtmlBody = null; // consume: only used for the initial draft restore
                LoadHtmlIntoEditorRequested?.Invoke(htmlToLoad);
                break;

            case (ComposeMode.Markdown, ComposeMode.Html):
                LoadHtmlIntoEditorRequested?.Invoke(_markdown.ToHtml(Body));
                break;

            case (ComposeMode.Markdown, ComposeMode.PlainText):
                Body = _markdown.ToPlainText(Body);
                break;

            case (ComposeMode.Html, ComposeMode.Markdown):
                var mdSnap = RichBodyProvider?.Invoke() ?? RichBodySnapshot.Empty;
                if (mdSnap.IsEmpty && RichBodyProvider != null)
                    LogService.Debug("SetMode Html→Markdown: provider returned empty snapshot");
                Body = mdSnap.Markdown;
                break;

            case (ComposeMode.Html, ComposeMode.PlainText):
                var ptSnap = RichBodyProvider?.Invoke() ?? RichBodySnapshot.Empty;
                if (ptSnap.IsEmpty && RichBodyProvider != null)
                    LogService.Debug("SetMode Html→PlainText: provider returned empty snapshot");
                Body = ptSnap.PlainText;
                break;
        }

        CurrentMode = newMode;
        return true;
    }

    private bool HasFormattingWorthConfirming() => CurrentMode switch
    {
        ComposeMode.Markdown => !string.IsNullOrWhiteSpace(Body) && Body != _markdown.ToPlainText(Body),
        ComposeMode.Html     => !(RichBodyProvider?.Invoke() ?? RichBodySnapshot.Empty).IsEmpty,
        _                    => false,
    };

    /// <summary>Renders the current Markdown body as a full HTML document for the preview pane.</summary>
    public string RenderPreviewHtml() => _markdown.WrapDocument(_markdown.ToHtml(Body), Subject);

    /// <summary>Returns the rendered HTML body fragment for the preview window, without any wrapper or styles.</summary>
    public string GetBodyHtml() => CurrentMode switch
    {
        ComposeMode.Markdown => _markdown.ToHtml(Body),
        ComposeMode.Html     => (RichBodyProvider?.Invoke() ?? RichBodySnapshot.Empty).Html,
        _                    => string.Empty,
    };

    /// <summary>Called by the View when the rich editor content changes (RichTextBox has no Body binding).</summary>
    public void MarkBodyDirty() => _isDirty = true;

    /// <summary>
    /// Opens the template picker. The View subscribes to this event to show the dialog.
    /// </summary>
    public event Func<Task<MessageTemplate?>>? InsertTemplateRequested;

    [RelayCommand]
    private async Task InsertTemplateAsync()
    {
        if (InsertTemplateRequested == null) return;
        var template = await InsertTemplateRequested();
        if (template == null) return;

        var displayName = !string.IsNullOrWhiteSpace(SenderAccount?.DisplayName)
            ? SenderAccount!.DisplayName
            : !string.IsNullOrWhiteSpace(SenderAccount?.Username)
                ? SenderAccount!.Username
                : string.Empty;
        var now = DateTime.Now;

        var body = template.Body
            .Replace("{sender}", displayName, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("d"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("t"), StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(template.Subject) && string.IsNullOrWhiteSpace(Subject))
            Subject = template.Subject
                .Replace("{sender}", displayName, StringComparison.OrdinalIgnoreCase)
                .Replace("{date}", now.ToString("d"), StringComparison.OrdinalIgnoreCase)
                .Replace("{time}", now.ToString("t"), StringComparison.OrdinalIgnoreCase);

        if (CurrentMode == ComposeMode.Html)
            InsertTextIntoEditorRequested?.Invoke(body);
        else
            Body += body;
        SetStatusOutcome($"Template '{template.Title}' inserted.");
    }

    [RelayCommand]
    private async Task SaveAsTemplateAsync()
    {
        // Templates are plain-text only — in HTML mode use the editor's text rendering.
        var templateBody = CurrentMode == ComposeMode.Html
            ? (RichBodyProvider?.Invoke() ?? RichBodySnapshot.Empty).PlainText
            : Body;
        if (string.IsNullOrWhiteSpace(templateBody))
        {
            SetStatusOutcome("Nothing to save — body is empty.");
            return;
        }

        var firstLine = templateBody.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "Untitled";
        if (firstLine.Length > 80) firstLine = firstLine[..80].TrimEnd();
        var suggestedTitle = Subject.Trim().Length > 0 ? Subject.Trim() : firstLine;
        var title = PromptTemplateNameRequested?.Invoke(suggestedTitle)?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return;

        var template = new MessageTemplate
        {
            Title = title,
            Subject = Subject,
            Body = templateBody
        };

        await _templateService.AddAsync(template);
        SetStatusOutcome($"Template saved as '{template.Title}'.");
    }

    /// <summary>
    /// Set by the View to show a multi-select Open File dialog (CLAUDE.md MVVM rules:
    /// Win32 dialogs are View-layer). Returns the chosen paths, or null when cancelled
    /// or unwired (headless/tests).
    /// </summary>
    public Func<string[]?>? OpenFilePathsRequested { get; set; }

    /// <summary>View-owned prompt used to name a template before it is persisted.</summary>
    public Func<string, string?>? PromptTemplateNameRequested { get; set; }
    public Func<DateTime, DateTime?>? PromptScheduleTimeRequested { get; set; }

    [RelayCommand]
    private async Task AddAttachmentsAsync()
    {
        var paths = OpenFilePathsRequested?.Invoke();
        if (paths == null) return;
        foreach (var path in paths)
            await AddAttachmentFromPathAsync(path);
    }

    /// <summary>
    /// Adds a file as an attachment (shared by AddAttachmentsCommand and clipboard paste).
    /// Reads asynchronously so large attachments don't freeze the window on slow disks.
    /// </summary>
    public async Task AddAttachmentFromPathAsync(string path)
    {
        if (!File.Exists(path)) return;
        var info  = new FileInfo(path);
        var bytes = await File.ReadAllBytesAsync(path);
        AddAttachmentFromContent(info.Name, bytes,
            AttachmentModel.ContentTypeFromFileName(info.Name), info.FullName);
    }

    /// <summary>
    /// Adds attachment bytes supplied by an editor-hosted file drop. WebView2 deliberately does
    /// not expose the source file's local path, so the HTML editor transfers its name and bytes.
    /// </summary>
    public void AddAttachmentFromContent(
        string fileName, byte[] content, string? contentType = null, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var safeFileName = AttachmentSafety.SanitizeFileName(Path.GetFileName(fileName));
        Attachments.Add(new AttachmentModel
        {
            FileName = safeFileName,
            ContentType = string.IsNullOrWhiteSpace(contentType)
                ? AttachmentModel.ContentTypeFromFileName(safeFileName)
                : contentType,
            FileSize = content.LongLength,
            PartSpecifier = sourcePath,
            Content = content,
        });
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentModel? attachment)
    {
        if (attachment != null)
            Attachments.Remove(attachment);
    }

    private void WarnIfAttachmentsExceedRecommendedSize()
    {
        var totalBytes = Attachments.Sum(attachment => attachment.FileSize);
        if (totalBytes <= AttachmentWarningThresholdBytes) return;

        WarningDialogRequested?.Invoke(
            $"The attachments total {totalBytes / 1_000_000.0:F1} MB. Many mail servers reject " +
            "messages larger than 25 MB, but QuickMail will continue with this operation.",
            "Large Attachments");
    }

    /// <summary>e.g. "3 files, 1.8 MB" or a warning above the common 25 MB threshold.</summary>
    public string AttachmentSummaryText
    {
        get
        {
            var count = Attachments.Count;
            if (count == 0) return string.Empty;
            var totalBytes = Attachments.Sum(a => a.FileSize);
            var totalDisplay = totalBytes >= 1_048_576
                ? $"{totalBytes / 1_048_576.0:F1} MB"
                : $"{totalBytes / 1_024.0:F0} KB";
            var warning = totalBytes > AttachmentWarningThresholdBytes
                ? " — warning: above 25 MB"
                : string.Empty;
            return $"{count} file{(count == 1 ? "" : "s")}, {totalDisplay}{warning}";
        }
    }

    [RelayCommand]
    private async Task OpenComposeAttachmentAsync(AttachmentModel? attachment)
    {
        if (attachment?.Content == null) return;

        // Sanitized: forwarded/replied attachments carry server-supplied names, so a
        // crafted name (path separators, absolute path) must not escape the temp folder.
        var safeFileName = AttachmentSafety.SanitizeFileName(attachment.FileName);

        if (AttachmentSafety.IsDangerousExtension(safeFileName))
        {
            // CLAUDE.md MVVM Rules: ViewModels must not call MessageBox directly.
            // If the View hasn't wired a confirmation handler, treat that as deny so
            // we never silently open something potentially dangerous.
            var confirmed = ConfirmationRequested?.Invoke(
                $"'{safeFileName}' is an executable file type. Opening it could be dangerous. Continue?",
                "Security Warning") ?? false;
            if (!confirmed) return;
        }

        // Per-attachment subfolder so two files with the same name (invoice.pdf, invoice.pdf
        // from different messages or sessions) don't overwrite each other in %TEMP%\QuickMail.
        var tempDir = Path.Combine(Path.GetTempPath(), "QuickMail", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, safeFileName);
        await File.WriteAllBytesAsync(tempPath, attachment.Content);
        Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
    }

    internal ComposeModel BuildComposeModel(Guid accountId)
    {
        // Resolve the body parts for the active mode. Markdown mode sends the
        // markdown source as the text/plain part (it reads naturally as text);
        // HTML mode sends the stripped plain text. An effectively empty rich body
        // falls back to text/plain only.
        var body = Body;
        string? htmlBody = null;
        switch (CurrentMode)
        {
            case ComposeMode.Markdown when !string.IsNullOrWhiteSpace(Body):
                htmlBody = _markdown.WrapDocument(_markdown.ToHtml(Body), Subject);
                break;

            case ComposeMode.Html:
                if (RichBodyProvider == null)
                    throw new InvalidOperationException("RichBodyProvider must be set before sending in HTML mode.");
                var snapshot = RichBodyProvider.Invoke();
                if (!snapshot.IsEmpty)
                {
                    // Sanitize again at the trust boundary: pasted HTML enters the
                    // contenteditable DOM after the initial load and may contain active
                    // elements or event attributes. Preserve mail CSS/tables/images but
                    // never send scripts or handlers onward.
                    htmlBody = MessageBodyHtmlBuilder.BuildMessageHtml(new MailMessageDetail
                    {
                        Subject = Subject,
                        HtmlBody = snapshot.Html,
                        PlainTextBody = snapshot.PlainText,
                    });
                    body     = MessageBodyHtmlBuilder.HtmlToText(htmlBody);
                    // The WebView editor supplies a complete HTML document so do not
                    // wrap it again and discard its original head/CSS.
                }
                break;
        }

        var model = new ComposeModel
        {
            AccountId           = accountId,
            To                  = To,
            Cc                  = Cc,
            Bcc                 = Bcc,
            Subject             = Subject,
            Body                = body,
            Mode                = CurrentMode,
            SpellLanguage       = SpellLanguage,
            HtmlBody            = htmlBody,
            InReplyToMessageId  = _inReplyToMessageId,
            ReplySourceAccountId = _replySourceAccountId,
            ReplySourceFolderName = _replySourceFolderName,
            ReplySourceMessageId = _replySourceMessageId,
            DraftMessageId      = _draftMessageId,
            DraftFolderName     = _draftFolderName,
            Attachments         = Attachments.ToList(),
        };
        return model;
    }

    // ── Factory helpers ────────────────────────────────────────────────────────

    public static ComposeModel CreateReply(MailMessageDetail detail, Guid accountId)
    {
        var subject = detail.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? detail.Subject
            : $"Re: {detail.Subject}";

        var attribution = $"\n\nOn {detail.Date.ToLocalTime():f}, {detail.From} wrote:\n";

        // Fall back to HTML→text conversion when the message has no plain-text part.
        // HTML-only messages otherwise reply with an empty quote — the attribution
        // line with nothing under it (issue #260).
        var plainBody = string.IsNullOrEmpty(detail.PlainTextBody) && !string.IsNullOrEmpty(detail.HtmlBody)
            ? HtmlStripper.ToPlainText(detail.HtmlBody)
            : detail.PlainTextBody;

        var quoted = string.Join("\n", System.Array.ConvertAll(
            plainBody.Split('\n'),
            line => "> " + line));

        var model = new ComposeModel
        {
            Kind      = ComposeKind.Reply,
            AccountId = accountId,
            To = string.IsNullOrEmpty(detail.ReplyTo) ? detail.From : detail.ReplyTo,
            Subject = subject,
            Body = attribution + quoted,
            InReplyToMessageId = detail.InternetMessageId,
            ReplySourceAccountId = detail.Direction == MessageDirection.Outgoing ? null : detail.AccountId,
            ReplySourceFolderName = detail.Direction == MessageDirection.Outgoing ? null : detail.FolderName,
            ReplySourceMessageId = detail.Direction == MessageDirection.Outgoing ? null : detail.MessageId,
        };

        // Keep the original rich body for HTML replies. Previously Reply always
        // flattened the message to quoted plain text, so selecting HTML mode could
        // only turn that already-damaged text back into a few paragraphs. Body stays
        // populated as the safe/plain alternative for explicit Plain Text mode.
        if (!string.IsNullOrEmpty(detail.HtmlBody))
        {
            model.HtmlBody = BuildReplyHtmlDocument(detail);
            model.Mode = ComposeMode.Html;
        }

        return model;
    }

    /// <param name="ownAddress">The sender's own email address; excluded from the Cc list to avoid self-addressing.</param>
    public static ComposeModel CreateReplyAll(MailMessageDetail detail, Guid accountId, string ownAddress = "")
    {
        var model = CreateReply(detail, accountId);

        // Also exclude whichever address landed in model.To (the original From or ReplyTo).
        // Otherwise mailing-list senders who were Cc'd on their own message appear on both
        // the To and Cc lines of the reply-all.
        var toAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (InternetAddressList.TryParse(model.To ?? string.Empty, out var modelToList))
        {
            foreach (var a in modelToList.OfType<MailboxAddress>())
                toAddresses.Add(a.Address);
        }
        if (!string.IsNullOrEmpty(ownAddress))
            toAddresses.Add(ownAddress);

        // Merge original To + Cc, excluding the sender's own address and the To recipient,
        // into the new Cc. Use TryParse so empty/malformed address strings return an empty
        // list rather than throwing MimeKit.ParseException.
        _ = InternetAddressList.TryParse(detail.To ?? string.Empty, out var toList);
        _ = InternetAddressList.TryParse(detail.Cc ?? string.Empty, out var ccList);
        var recipients = (toList ?? [])
            .Concat(ccList ?? [])
            .OfType<MailboxAddress>()
            .Where(a => !toAddresses.Contains(a.Address))
            .GroupBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        model.Cc   = string.Join(", ", recipients.Select(a => a.ToString()));
        model.Kind = ComposeKind.ReplyAll;
        return model;
    }

    public static ComposeModel CreateForward(MailMessageDetail detail, Guid accountId)
    {
        var subject = detail.Subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase)
                   || detail.Subject.StartsWith("FW:", StringComparison.OrdinalIgnoreCase)
            ? detail.Subject
            : $"Fwd: {detail.Subject}";

        var header = $"\n\n---------- Forwarded message ----------\n"
                   + $"From: {detail.From}\n"
                   + $"Date: {detail.Date.ToLocalTime():f}\n"
                   + $"Subject: {detail.Subject}\n"
                   + $"To: {detail.To}\n\n";

        // Fall back to HTML→text conversion when the message has no plain-text part.
        var plainBody = string.IsNullOrEmpty(detail.PlainTextBody) && !string.IsNullOrEmpty(detail.HtmlBody)
            ? HtmlStripper.ToPlainText(detail.HtmlBody)
            : detail.PlainTextBody;

        var model = new ComposeModel
        {
            Kind      = ComposeKind.Forward,
            AccountId = accountId,
            Subject   = subject,
            Body      = header + plainBody,
        };

        if (!string.IsNullOrEmpty(detail.HtmlBody))
        {
            model.HtmlBody = BuildForwardedHtmlBlock(detail);
            model.Mode     = ComposeMode.Html;
        }

        return model;
    }

    /// <summary>Creates an independent editable copy. No reply/thread or stored-message identity is retained.</summary>
    public static ComposeModel CreateSendAgain(MailMessageDetail detail, Guid accountId) => new()
    {
        Kind = ComposeKind.NewMessage,
        AccountId = accountId,
        To = detail.To,
        Cc = detail.Cc,
        Bcc = detail.Bcc,
        Subject = detail.Subject,
        Body = string.IsNullOrEmpty(detail.PlainTextBody) && !string.IsNullOrEmpty(detail.HtmlBody)
            ? HtmlStripper.ToPlainText(detail.HtmlBody)
            : detail.PlainTextBody,
        HtmlBody = string.IsNullOrWhiteSpace(detail.HtmlBody) ? null : detail.HtmlBody,
        Mode = string.IsNullOrWhiteSpace(detail.HtmlBody) ? ComposeMode.PlainText : ComposeMode.Html,
        SpellLanguage = detail.DraftSpellLanguage,
    };

    private static string BuildForwardedHtmlBlock(MailMessageDetail detail)
    {
        // Strip outer html/head/body wrappers so we don't nest them inside the blockquote.
        var body = StripHtmlWrappers(detail.HtmlBody);
        var date = detail.Date.ToLocalTime().ToString("f");
        return $"""
            <p>&#160;</p>
            <div>
              <p>---------- Forwarded message ----------<br />
              From: {WebUtility.HtmlEncode(detail.From)}<br />
              Date: {WebUtility.HtmlEncode(date)}<br />
              Subject: {WebUtility.HtmlEncode(detail.Subject)}<br />
              To: {WebUtility.HtmlEncode(detail.To)}</p>
            </div>
            <blockquote style="border-left: 2px solid #ccc; padding-left: 8px; margin-left: 4px;">
            {body}
            </blockquote>
            """;
    }

    private static string BuildReplyHtmlDocument(MailMessageDetail detail)
    {
        var html = detail.HtmlBody ?? string.Empty;
        var attribution = WebUtility.HtmlEncode(
            $"On {detail.Date.ToLocalTime():f}, {detail.From} wrote:");
        var prefix = $"<p><br></p><p>{attribution}</p>";
        var bodyStart = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyStart >= 0)
        {
            var tagEnd = IndexOfTagClose(html, bodyStart);
            if (tagEnd >= 0) return html.Insert(tagEnd + 1, prefix);
        }
        return $"<!doctype html><html><head><meta charset=\"utf-8\"></head><body>{prefix}{html}</body></html>";
    }

    private static string StripHtmlWrappers(string html)
    {
        // Remove leading <!DOCTYPE...> and <html...>...</html> wrapper if present so the
        // fragment can be embedded safely inside a blockquote without double html/body nesting.
        var s = html.Trim();

        // Strip <!DOCTYPE ...>
        if (s.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            var end = s.IndexOf('>');
            if (end >= 0) s = s[(end + 1)..].TrimStart();
        }

        // Extract content of <body>…</body> if present.
        var bodyStart = s.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyStart >= 0)
        {
            // Scan past quoted attribute values to find the true end of the opening tag.
            var bodyTagEnd = IndexOfTagClose(s, bodyStart);
            if (bodyTagEnd >= 0)
            {
                var bodyClose = s.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                s = bodyClose > bodyTagEnd
                    ? s[(bodyTagEnd + 1)..bodyClose]
                    : s[(bodyTagEnd + 1)..];
            }
        }
        else
        {
            // No <body> — try stripping the outer <html>…</html> wrapper.
            var htmlStart  = s.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
            var htmlTagEnd = htmlStart >= 0 ? IndexOfTagClose(s, htmlStart) : -1;
            var htmlClose  = s.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
            if (htmlTagEnd >= 0 && htmlClose > htmlTagEnd)
                s = s[(htmlTagEnd + 1)..htmlClose];
            else if (htmlTagEnd >= 0)
                s = s[(htmlTagEnd + 1)..];
        }

        return s.Trim();
    }

    // Scans forward from the start of a tag and returns the index of the closing '>'
    // of that tag's opening sequence, skipping any '>' characters inside quoted attribute values.
    private static int IndexOfTagClose(string s, int tagStart)
    {
        bool inQuote = false;
        char quoteChar = '\0';
        for (int i = tagStart; i < s.Length; i++)
        {
            char c = s[i];
            if (inQuote)
            {
                if (c == quoteChar) inQuote = false;
            }
            else if (c == '"' || c == '\'')
            {
                inQuote = true;
                quoteChar = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }
        return -1;
    }
}
