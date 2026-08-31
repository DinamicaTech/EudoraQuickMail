using System.Text.Json;
using System.IO;
using QuickMail.Models;

namespace QuickMail.Services;

public sealed record ScheduledMail(Guid Id, DateTimeOffset SendAtUtc, ComposeModel Message, int Attempts = 0,
    string? LastError = null, string? LocalMessageId = null);

/// <summary>Durable local outbox with a short periodic dispatcher.</summary>
public sealed class ScheduledSendService : IDisposable
{
    public event Action<MailOperationFailure, bool>? Failed;
    public event Action<Guid>? SentMailChanged;
    private readonly string _path;
    private readonly ISendMailService _sender;
    private readonly IAccountService _accounts;
    private readonly ICredentialService _credentials;
    private readonly LocalStoreService _store;
    private readonly IMailService _mail;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;

    public ScheduledSendService(ProfileContext profile, ISendMailService sender, IAccountService accounts,
        ICredentialService credentials, LocalStoreService store, IMailService mail)
    {
        _path = Path.Combine(profile.ProfileDir, "scheduled-mail.json");
        _sender = sender;
        _accounts = accounts;
        _credentials = credentials;
        _store = store;
        _mail = mail;
        _timer = new Timer(_ => _ = DispatchDueAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    public async Task<Guid> ScheduleAsync(ComposeModel message, DateTimeOffset sendAtUtc)
    {
        if (sendAtUtc <= DateTimeOffset.UtcNow) throw new ArgumentOutOfRangeException(nameof(sendAtUtc));
        await _gate.WaitAsync();
        try
        {
            var queue = await LoadAsync();
            var id = Guid.NewGuid();
            var localId = "scheduled-" + id.ToString("N");
            var account = _accounts.LoadAccounts().FirstOrDefault(a => a.Id == message.AccountId)
                ?? throw new InvalidOperationException("Sender account not found.");
            await _store.EnsureLocalSystemFoldersAsync([account]);
            await _store.SaveLocalMessageAsync(new MailMessageDetail
            {
                AccountId = message.AccountId, FolderName = "Scheduled", MessageId = localId,
                From = account.Username, To = message.To, Cc = message.Cc, Bcc = message.Bcc, Subject = message.Subject,
                Date = sendAtUtc, PlainTextBody = message.Body, HtmlBody = message.HtmlBody ?? string.Empty,
                DraftComposeMode = message.Mode, DraftSpellLanguage = message.SpellLanguage,
                Preview = message.Body.Length <= 240 ? message.Body : message.Body[..240], IsRead = true,
                Direction = MessageDirection.Outgoing,
            });
            queue.Add(new ScheduledMail(id, sendAtUtc.ToUniversalTime(), message, LocalMessageId: localId));
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

    public async Task ReplaceAsync(Guid id, ComposeModel message, DateTimeOffset sendAtUtc)
    {
        if (sendAtUtc <= DateTimeOffset.Now) throw new ArgumentOutOfRangeException(nameof(sendAtUtc));
        await _gate.WaitAsync();
        try
        {
            var queue = await LoadAsync();
            var index = queue.FindIndex(x => x.Id == id);
            if (index < 0) throw new InvalidOperationException("Scheduled message no longer exists.");
            var old = queue[index];
            var localMessageId = old.LocalMessageId ?? "scheduled-" + old.Id.ToString("N");
            var account = _accounts.LoadAccounts().First(a => a.Id == message.AccountId);
            await _store.EnsureLocalSystemFoldersAsync([account]);
            await _store.SaveLocalMessageAsync(new MailMessageDetail
            {
                AccountId = message.AccountId, FolderName = "Scheduled", MessageId = localMessageId,
                From = account.Username, To = message.To, Cc = message.Cc, Bcc = message.Bcc, Subject = message.Subject,
                Date = sendAtUtc, PlainTextBody = message.Body, HtmlBody = message.HtmlBody ?? string.Empty,
                DraftComposeMode = message.Mode, DraftSpellLanguage = message.SpellLanguage,
                Preview = message.Body.Length <= 240 ? message.Body : message.Body[..240], IsRead = true,
                Direction = MessageDirection.Outgoing,
            });
            queue[index] = old with { SendAtUtc = sendAtUtc.ToUniversalTime(), Message = message,
                Attempts = 0, LastError = null, LocalMessageId = localMessageId };
            await SaveAsync(queue);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid id, bool deleteLocalCopy)
    {
        await _gate.WaitAsync();
        try
        {
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
        await _gate.WaitAsync(_stop.Token);
        try
        {
            var queue = await LoadAsync();
            var changed = false;
            foreach (var item in queue.Where(x => x.SendAtUtc <= DateTimeOffset.UtcNow).ToList())
            {
                var account = _accounts.LoadAccounts().FirstOrDefault(a => a.Id == item.Message.AccountId && a.IsActive);
                if (account is null) continue;
                try
                {
                    var password = account.BackendKind == BackendKind.Pop3Smtp ? null : _credentials.GetPassword(account.Id);
                    await _sender.SendAsync(item.Message, account, password, _stop.Token);
                    try
                    {
                        // SMTP acceptance is authoritative. Saving/synchronizing the Sent copy is
                        // best effort: if it fails we must still dequeue the item, otherwise the
                        // next timer pass sends a duplicate to the recipient.
                        await _mail.AppendToSentAsync(account.Id, item.Message, _stop.Token);
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
                        }
                        catch (Exception ex)
                        {
                            // Delivery succeeded: a local marker failure must not leave the item
                            // queued and cause a duplicate SMTP send on the next pass.
                            LogService.Log($"Scheduled send {item.Id}: original reply marker failed", ex);
                        }
                    }
                    if (item.LocalMessageId is not null)
                        await _store.DeleteLocalMessagesAsync(item.Message.AccountId, "Scheduled", [item.LocalMessageId], _stop.Token);
                    queue.Remove(item);
                    changed = true;
                    SentMailChanged?.Invoke(account.Id);
                }
                catch (Exception ex)
                {
                    var failure = new MailOperationFailure(account, "Send scheduled email (SMTP)", ex);
                    failures.Add(failure);
                    Failed?.Invoke(failure, manual);
                    var index = queue.IndexOf(item);
                    queue[index] = item with { Attempts = item.Attempts + 1, LastError = ex.Message };
                    changed = true;
                    LogService.Log($"Scheduled send failed ({item.Id})", ex);
                }
            }
            if (changed) await SaveAsync(queue);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { _gate.Release(); }
        return failures;
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
        _timer.Dispose();
        // DispatchDueAsync may still resume through its finally block. Cancelling is sufficient at
        // application exit; disposing the CTS/gate underneath that continuation causes shutdown
        // ObjectDisposedException dialogs.
        try { _stop.Cancel(); } catch { /* best effort at shutdown */ }
    }
}
