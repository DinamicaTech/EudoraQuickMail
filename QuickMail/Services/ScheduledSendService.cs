using System.Text.Json;
using System.IO;
using System.Collections.Concurrent;
using QuickMail.Models;

namespace QuickMail.Services;

public sealed record ScheduledMail(Guid Id, DateTimeOffset SendAtUtc, ComposeModel Message, int Attempts = 0,
    string? LastError = null, string? LocalMessageId = null, bool IsImmediate = false,
    DateTimeOffset? NextAttemptUtc = null, bool AutomaticRetry = true,
    bool TransportAccepted = false);

/// <summary>Durable local outbox with a short periodic dispatcher.</summary>
public sealed class ScheduledSendService : IOutgoingMailQueue, IDisposable
{
    public event Action<MailOperationFailure, bool>? Failed;
    public event Action<Guid>? SentMailChanged;
    public event Action<Guid, string, string, string?>? OriginalMessageReplied;
    public event Action<string>? ProgressChanged;
    private readonly string _path;
    private readonly ISendMailService _sender;
    private readonly IAccountService _accounts;
    private readonly ICredentialService _credentials;
    private readonly LocalStoreService _store;
    private readonly IMailService _mail;
    private readonly MailActivityPolicyService? _activity;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;

    public ScheduledSendService(ProfileContext profile, ISendMailService sender, IAccountService accounts,
        ICredentialService credentials, LocalStoreService store, IMailService mail,
        MailActivityPolicyService? activity = null)
    {
        _path = Path.Combine(profile.ProfileDir, "scheduled-mail.json");
        _sender = sender;
        _accounts = accounts;
        _credentials = credentials;
        _store = store;
        _mail = mail;
        _activity = activity;
        if (_activity != null) _activity.Changed += OnActivityChanged;
        _timer = new Timer(_ => RequestDispatch(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    public Task<Guid> ScheduleAsync(ComposeModel message, DateTimeOffset sendAtUtc,
        CancellationToken ct = default)
    {
        if (sendAtUtc <= DateTimeOffset.UtcNow) throw new ArgumentOutOfRangeException(nameof(sendAtUtc));
        return EnqueueAsync(message, sendAtUtc.ToUniversalTime(), false, null, ct);
    }

    public async Task<Guid> QueueImmediateAsync(ComposeModel message, Guid? replaceScheduledId = null,
        DateTimeOffset? notBeforeUtc = null, CancellationToken ct = default)
    {
        var sendAtUtc = (notBeforeUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var id = await EnqueueAsync(message, sendAtUtc, true, replaceScheduledId, ct);
        var remaining = sendAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            RequestDispatch();
        else
            RequestDispatchAfter(remaining);
        return id;
    }

    private async Task<Guid> EnqueueAsync(ComposeModel message, DateTimeOffset sendAtUtc, bool immediate,
        Guid? replaceScheduledId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var queue = await LoadAsync();
            var existingIndex = replaceScheduledId is { } existingId
                ? queue.FindIndex(item => item.Id == existingId)
                : -1;
            if (replaceScheduledId is not null && existingIndex < 0)
                throw new InvalidOperationException("Scheduled message no longer exists.");
            if (replaceScheduledId is { } sendingId && _inFlight.ContainsKey(sendingId))
                throw new InvalidOperationException("This message is already being sent.");

            var id = existingIndex >= 0 ? queue[existingIndex].Id : Guid.NewGuid();
            var localId = existingIndex >= 0
                ? queue[existingIndex].LocalMessageId ?? "scheduled-" + id.ToString("N")
                : "scheduled-" + id.ToString("N");
            var account = _accounts.LoadAccounts().FirstOrDefault(a => a.Id == message.AccountId)
                ?? throw new InvalidOperationException("Sender account not found.");
            await _store.EnsureLocalSystemFoldersAsync([account]);
            await SaveScheduledLocalCopyAsync(account, message, localId, sendAtUtc);
            var queued = new ScheduledMail(id, sendAtUtc, message, LocalMessageId: localId,
                IsImmediate: immediate);
            if (existingIndex >= 0) queue[existingIndex] = queued;
            else queue.Add(queued);
            await SaveAsync(queue);
            return id;
        }
        finally { _gate.Release(); }
    }

    public async Task<ScheduledMail?> FindByLocalMessageIdAsync(string localMessageId)
    {
        await _gate.WaitAsync();
        try { return (await LoadAsync()).FirstOrDefault(x => x.LocalMessageId == localMessageId); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ScheduledMail>> GetSnapshotAsync()
    {
        await _gate.WaitAsync();
        try { return (await LoadAsync()).ToList(); }
        finally { _gate.Release(); }
    }

    public async Task ReplaceAsync(Guid id, ComposeModel message, DateTimeOffset sendAtUtc,
        CancellationToken ct = default)
    {
        if (sendAtUtc <= DateTimeOffset.Now) throw new ArgumentOutOfRangeException(nameof(sendAtUtc));
        await _gate.WaitAsync(ct);
        try
        {
            if (_inFlight.ContainsKey(id))
                throw new InvalidOperationException("This message is already being sent.");
            var queue = await LoadAsync();
            var index = queue.FindIndex(x => x.Id == id);
            if (index < 0) throw new InvalidOperationException("Scheduled message no longer exists.");
            var old = queue[index];
            var localMessageId = old.LocalMessageId ?? "scheduled-" + old.Id.ToString("N");
            var account = _accounts.LoadAccounts().First(a => a.Id == message.AccountId);
            await _store.EnsureLocalSystemFoldersAsync([account]);
            await SaveScheduledLocalCopyAsync(account, message, localMessageId, sendAtUtc);
            queue[index] = old with { SendAtUtc = sendAtUtc.ToUniversalTime(), Message = message,
                Attempts = 0, LastError = null, LocalMessageId = localMessageId, IsImmediate = false,
                NextAttemptUtc = null, AutomaticRetry = true };
            await SaveAsync(queue);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid id, bool deleteLocalCopy)
    {
        await _gate.WaitAsync();
        try
        {
            if (_inFlight.ContainsKey(id))
                throw new InvalidOperationException("This message is already being sent.");
            var queue = await LoadAsync();
            var item = queue.FirstOrDefault(x => x.Id == id);
            if (item is null) return;
            if (deleteLocalCopy && item.LocalMessageId is not null)
                await _store.DeleteLocalMessagesAsync(item.Message.AccountId, "Scheduled", [item.LocalMessageId]);
            queue.Remove(item);
            await SaveAsync(queue);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MailOperationFailure>> DispatchDueAsync(bool manual = false)
    {
        var failures = new List<MailOperationFailure>();
        if (_activity is { CanSend: false }) return failures;
        await _dispatchGate.WaitAsync(_stop.Token);
        try
        {
            var attempted = new HashSet<Guid>();
            while (!_stop.IsCancellationRequested)
            {
                ScheduledMail? item;
                await _gate.WaitAsync(_stop.Token);
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    item = (await LoadAsync()).FirstOrDefault(candidate =>
                        !attempted.Contains(candidate.Id)
                        && (candidate.TransportAccepted || (candidate.SendAtUtc <= now
                            && (manual || (candidate.AutomaticRetry
                                && (candidate.NextAttemptUtc ?? candidate.SendAtUtc) <= now)))));
                    if (item is not null)
                    {
                        attempted.Add(item.Id);
                        _inFlight.TryAdd(item.Id, 0);
                    }
                }
                finally { _gate.Release(); }

                if (item is null) break;

                var account = _accounts.LoadAccounts().FirstOrDefault(a =>
                    a.Id == item.Message.AccountId && (a.IsActive || item.TransportAccepted));
                if (account is null)
                {
                    await UpdateQueuedItemAsync(item.Id, current => current with
                    {
                        Attempts = current.Attempts + 1,
                        LastError = "The sender account is missing or inactive.",
                        AutomaticRetry = false,
                        NextAttemptUtc = null,
                    });
                    LogService.Log($"Scheduled send {item.Id}: sender account is missing or inactive");
                    _inFlight.TryRemove(item.Id, out _);
                    continue;
                }
                try
                {
                    var details = $"queueId={item.Id}; account={account.Username}; immediate={item.IsImmediate}; " +
                                  $"attempt={item.Attempts + 1}; attachments={item.Message.Attachments.Count}";
                    var itemStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    PerformanceLogService.Marker("Outgoing queue: dispatch START", details);
                    if (!item.TransportAccepted)
                    {
                        ProgressChanged?.Invoke($"Sending queued message from {account.AccountLabel}…");
                        var password = account.BackendKind == BackendKind.Pop3Smtp
                            ? null : _credentials.GetPassword(account.Id);
                        await _sender.SendAsync(item.Message, account, password, _stop.Token);

                        // Persist SMTP acceptance before any best-effort housekeeping. If the app
                        // closes or crashes afterwards, the next run completes cleanup without ever
                        // submitting the same message to the recipient a second time.
                        item = item with
                        {
                            TransportAccepted = true,
                            LastError = null,
                            NextAttemptUtc = null,
                            AutomaticRetry = false,
                        };
                        await UpdateQueuedItemAsync(item.Id, _ => item);
                        PerformanceLogService.Record("Outgoing queue: SMTP accepted",
                            System.Diagnostics.Stopwatch.GetElapsedTime(itemStarted), details);
                    }
                    else
                    {
                        ProgressChanged?.Invoke($"Completing sent message from {account.AccountLabel}…");
                        PerformanceLogService.Marker("Outgoing queue: resume accepted item", details);
                    }
                    try
                    {
                        // SMTP acceptance is authoritative. Saving/synchronizing the Sent copy is
                        // best effort: if it fails we must still dequeue the item, otherwise the
                        // next timer pass sends a duplicate to the recipient.
                        await _mail.AppendToSentAsync(account.Id, item.Message, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        LogService.Log($"Scheduled send {item.Id}: failed to save Sent copy", ex);
                    }
                    if (item.Message.ReplySourceAccountId is { } sourceAccountId
                        && !string.IsNullOrWhiteSpace(item.Message.ReplySourceFolderName)
                        && !string.IsNullOrWhiteSpace(item.Message.ReplySourceMessageId))
                    {
                        try
                        {
                            await _store.UpdateIsRepliedAsync(sourceAccountId,
                                item.Message.ReplySourceFolderName, item.Message.ReplySourceMessageId,
                                item.Message.InReplyToMessageId);
                            OriginalMessageReplied?.Invoke(sourceAccountId,
                                item.Message.ReplySourceFolderName, item.Message.ReplySourceMessageId,
                                item.Message.InReplyToMessageId);
                        }
                        catch (Exception ex)
                        {
                            // Delivery succeeded: a local marker failure must not leave the item
                            // queued and cause a duplicate SMTP send on the next pass.
                            LogService.Log($"Scheduled send {item.Id}: original reply marker failed", ex);
                        }
                    }
                    if (item.LocalMessageId is not null)
                    {
                        try
                        {
                            await _store.DeleteLocalMessagesAsync(item.Message.AccountId, "Scheduled",
                                [item.LocalMessageId], CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            LogService.Log($"Scheduled send {item.Id}: failed to remove Scheduled copy", ex);
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(item.Message.DraftMessageId)
                        && !string.IsNullOrWhiteSpace(item.Message.DraftFolderName))
                    {
                        try
                        {
                            await _mail.MoveToTrashAsync(item.Message.AccountId, item.Message.DraftFolderName,
                                item.Message.DraftMessageId, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            LogService.Log($"Scheduled send {item.Id}: failed to move source draft to Trash", ex);
                        }
                    }
                    await RemoveQueuedItemAsync(item.Id);
                    SentMailChanged?.Invoke(account.Id);
                    ProgressChanged?.Invoke($"Message sent from {account.AccountLabel}.");
                    PerformanceLogService.Record("Outgoing queue: dispatch complete",
                        System.Diagnostics.Stopwatch.GetElapsedTime(itemStarted), details);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    var failure = new MailOperationFailure(account, "Send scheduled email (SMTP)", ex);
                    failures.Add(failure);
                    var attempts = item.Attempts + 1;
                    var automaticRetry = ShouldRetryAutomatically(ex);
                    await UpdateQueuedItemAsync(item.Id, current => current with
                    {
                        Attempts = attempts,
                        LastError = ex.Message,
                        AutomaticRetry = automaticRetry,
                        NextAttemptUtc = automaticRetry ? DateTimeOffset.UtcNow + RetryDelay(attempts) : null,
                    });
                    LogService.Log($"Scheduled send failed ({item.Id})", ex);
                    // Notify only after the durable queue contains the failure state, so opening
                    // Scheduled from the error dialog immediately shows "SMTP error" and details.
                    Failed?.Invoke(failure, manual);
                }
                finally
                {
                    _inFlight.TryRemove(item.Id, out _);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { _dispatchGate.Release(); }
        return failures;
    }

    private async Task UpdateQueuedItemAsync(Guid id, Func<ScheduledMail, ScheduledMail> update)
    {
        await _gate.WaitAsync();
        try
        {
            var queue = await LoadAsync();
            var index = queue.FindIndex(item => item.Id == id);
            if (index < 0) return;
            queue[index] = update(queue[index]);
            await SaveAsync(queue);
        }
        finally { _gate.Release(); }
    }

    public async Task<ComposeModel?> UndoSendAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Dispatch marks the item in-flight while still holding this same gate. Therefore an
            // undo either wins completely before SMTP starts, or loses cleanly without racing the
            // transport half-way through a send.
            if (_inFlight.ContainsKey(id)) return null;

            var queue = await LoadAsync();
            var item = queue.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null || item.TransportAccepted) return null;

            var account = _accounts.LoadAccounts().FirstOrDefault(candidate =>
                candidate.Id == item.Message.AccountId);
            if (account is null) return null;

            await _store.EnsureLocalSystemFoldersAsync([account]);
            var draftId = "draft-undo-" + id.ToString("N");
            await SaveUndoDraftLocalCopyAsync(account, item.Message, draftId);

            queue.Remove(item);
            await SaveAsync(queue);

            if (item.LocalMessageId is not null)
                await _store.DeleteLocalMessagesAsync(item.Message.AccountId, "Scheduled",
                    [item.LocalMessageId], ct);

            return item.Message;
        }
        finally { _gate.Release(); }
    }

    private async Task RemoveQueuedItemAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            var queue = await LoadAsync();
            if (queue.RemoveAll(item => item.Id == id) > 0)
                await SaveAsync(queue);
        }
        finally { _gate.Release(); }
    }

    private async Task SaveScheduledLocalCopyAsync(AccountModel account, ComposeModel message,
        string localMessageId, DateTimeOffset sendAtUtc)
    {
        await _store.SaveLocalMessageAsync(new MailMessageDetail
        {
            AccountId = message.AccountId, FolderName = "Scheduled", MessageId = localMessageId,
            From = account.Username, To = message.To, Cc = message.Cc, Bcc = message.Bcc,
            Subject = message.Subject, Date = sendAtUtc, PlainTextBody = message.Body,
            HtmlBody = message.HtmlBody ?? string.Empty, DraftComposeMode = message.Mode,
            DraftSpellLanguage = message.SpellLanguage,
            Preview = message.Body.Length <= 240 ? message.Body : message.Body[..240], IsRead = true,
            Direction = MessageDirection.Outgoing,
        });
    }

    private Task SaveUndoDraftLocalCopyAsync(AccountModel account, ComposeModel message,
        string localMessageId) =>
        _store.SaveLocalMessageAsync(new MailMessageDetail
        {
            AccountId = message.AccountId, FolderName = "Draft", MessageId = localMessageId,
            From = account.Username, To = message.To, Cc = message.Cc, Bcc = message.Bcc,
            Subject = message.Subject, Date = DateTimeOffset.Now, PlainTextBody = message.Body,
            HtmlBody = message.HtmlBody ?? string.Empty, DraftComposeMode = message.Mode,
            DraftSpellLanguage = message.SpellLanguage,
            Preview = message.Body.Length <= 240 ? message.Body : message.Body[..240], IsRead = true,
            Direction = MessageDirection.Outgoing,
        });

    private void RequestDispatch()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await DispatchDueAsync(); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { LogService.Log("Outgoing queue dispatcher failed", ex); }
        });
    }

    private void OnActivityChanged()
    {
        if (_activity?.CanSend == true) RequestDispatch();
    }

    private void RequestDispatchAfter(TimeSpan delay)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _stop.Token);
                RequestDispatch();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { LogService.Log("Delayed outgoing queue wake failed", ex); }
        });
    }

    private static TimeSpan RetryDelay(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(30),
    };

    private static bool ShouldRetryAutomatically(Exception error)
    {
        var description = string.Join(" ", Enumerate(error).Select(ex =>
            $"{ex.GetType().FullName} {ex.Message}")).ToLowerInvariant();
        string[] permanentHints =
        [
            "authentication", "authenticate", "credentials", "password", "invalid_grant",
            "invalid_client", "unauthorized_client", "access denied", "not authorized",
            "certificate", "hostname", "sender account", "mailbox unavailable", " 535",
        ];
        return !permanentHints.Any(description.Contains);

        static IEnumerable<Exception> Enumerate(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException!)
                yield return current;
        }
    }

    private async Task<List<ScheduledMail>> LoadAsync()
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<ScheduledMail>>(stream) ?? [];
    }

    private async Task SaveAsync(List<ScheduledMail> queue)
    {
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, queue);
        File.Move(temp, _path, true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_activity != null) _activity.Changed -= OnActivityChanged;
        _timer.Dispose();
        // DispatchDueAsync may still resume through its finally block. Cancelling is sufficient at
        // application exit; disposing the CTS/gate underneath that continuation causes shutdown
        // ObjectDisposedException dialogs.
        try { _stop.Cancel(); } catch { /* best effort at shutdown */ }
    }
}
