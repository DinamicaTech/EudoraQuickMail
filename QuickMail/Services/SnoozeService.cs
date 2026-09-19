using QuickMail.Models;

namespace QuickMail.Services;

public sealed record SnoozedMessageKey(Guid AccountId, string FolderName, string MessageId);

public sealed class SnoozeService : IDisposable
{
    private readonly ILocalMailboxStore _store;
    private readonly Timer _timer;
    private int _running;
    private int _disposed;

    public SnoozeService(ILocalMailboxStore store)
    {
        _store = store;
        _timer = new Timer(_ => _ = WakeDueAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));
    }

    public event Action<IReadOnlyList<MailMessageSummary>>? MessagesAwakened;

    public Task SnoozeAsync(IEnumerable<MailMessageSummary> messages, DateTimeOffset wakeAtUtc,
        CancellationToken ct = default) => _store.SnoozeMessagesAsync(messages.Select(message =>
            new SnoozedMessageKey(message.AccountId, message.FolderName, message.MessageId)).ToList(),
            wakeAtUtc.ToUniversalTime(), ct);

    public async Task WakeDueAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            var awakened = await _store.WakeDueSnoozedMessagesAsync(DateTimeOffset.UtcNow, ct);
            if (awakened.Count > 0) MessagesAwakened?.Invoke(awakened);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogService.Log("Wake snoozed messages", ex); }
        finally { Volatile.Write(ref _running, 0); }
    }

    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _timer.Dispose(); }
}
