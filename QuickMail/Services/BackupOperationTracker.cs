using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Tracks send/receive work which must drain before a maintenance shutdown.</summary>
public static class BackupOperationTracker
{
    private static readonly object Gate = new();
    private static int _active;
    private static bool _maintenance;
    public static bool Busy { get { lock (Gate) return _active != 0; } }
    public static void SetMaintenance(bool value) { lock (Gate) _maintenance = value; }
    public static bool TryBeginMaintenance()
    {
        lock (Gate)
        {
            if (_active != 0) return false;
            _maintenance = true; return true;
        }
    }
    public static IDisposable? TryEnter()
    {
        lock (Gate)
        {
            if (_maintenance) return null;
            _active++; return new Ticket();
        }
    }
    public static IDisposable Enter()
    {
        lock (Gate)
        {
            if (_maintenance) throw new OperationCanceledException("Application is preparing for backup maintenance.");
            _active++; return new Ticket();
        }
    }
    private sealed class Ticket : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) lock (Gate) _active--; }
    }
}
internal sealed class BackupAwareSender(ISendMailService inner) : ISendMailService
{
    public async Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default)
    {
        using var operation = BackupOperationTracker.Enter(); await inner.SendAsync(compose, account, password, ct);
    }
    public async Task SendIcsReplyAsync(string content, AccountModel account, string? password, string organizerEmail, CancellationToken ct = default)
    {
        using var operation = BackupOperationTracker.Enter(); await inner.SendIcsReplyAsync(content, account, password, organizerEmail, ct);
    }
    public Task VerifyAsync(AccountModel account, string? password, CancellationToken ct = default) => inner.VerifyAsync(account, password, ct);
}
