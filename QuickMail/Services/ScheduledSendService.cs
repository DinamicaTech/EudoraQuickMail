using System.Text.Json;
using System.IO;
using QuickMail.Models;

namespace QuickMail.Services;

public sealed record ScheduledMail(Guid Id, DateTimeOffset SendAtUtc, ComposeModel Message, int Attempts = 0,
    string? LastError = null);

/// <summary>Durable local outbox with a short periodic dispatcher.</summary>
public sealed class ScheduledSendService : IDisposable
{
    private readonly string _path;
    private readonly ISendMailService _sender;
    private readonly IAccountService _accounts;
    private readonly ICredentialService _credentials;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    public ScheduledSendService(ProfileContext profile, ISendMailService sender, IAccountService accounts,
        ICredentialService credentials)
    {
        _path = Path.Combine(profile.ProfileDir, "scheduled-mail.json");
        _sender = sender;
        _accounts = accounts;
        _credentials = credentials;
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
            queue.Add(new ScheduledMail(id, sendAtUtc.ToUniversalTime(), message));
            await SaveAsync(queue);
            return id;
        }
        finally { _gate.Release(); }
    }

    public async Task DispatchDueAsync()
    {
        await _gate.WaitAsync(_stop.Token);
        try
        {
            var queue = await LoadAsync();
            var changed = false;
            foreach (var item in queue.Where(x => x.SendAtUtc <= DateTimeOffset.UtcNow).ToList())
            {
                var account = _accounts.LoadAccounts().FirstOrDefault(a => a.Id == item.Message.AccountId);
                if (account is null) continue;
                try
                {
                    var password = account.BackendKind == BackendKind.Pop3Smtp ? null : _credentials.GetPassword(account.Id);
                    await _sender.SendAsync(item.Message, account, password, _stop.Token);
                    queue.Remove(item);
                    changed = true;
                }
                catch (Exception ex)
                {
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

    public void Dispose() { _stop.Cancel(); _timer.Dispose(); _stop.Dispose(); _gate.Dispose(); }
}
