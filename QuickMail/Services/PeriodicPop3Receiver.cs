using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Serial background POP3 sweep. Accounts opt in individually.</summary>
public sealed class PeriodicPop3Receiver : IDisposable
{
    private readonly IAccountService _accounts;
    private readonly IPop3ReceiveService _receiver;
    private readonly IRuleService? _rules;
    private readonly Timer _timer;
    private readonly CancellationTokenSource _stop = new();
    private int _running;

    public PeriodicPop3Receiver(IAccountService accounts, IPop3ReceiveService receiver,
        IRuleService? rules = null, TimeSpan? interval = null)
    {
        _accounts = accounts;
        _receiver = receiver;
        _rules = rules;
        var cadence = interval ?? TimeSpan.FromMinutes(5);
        _timer = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromSeconds(2), cadence);
    }

    public event Action<AccountModel, Pop3ReceiveResult>? Completed;
    public event Action<AccountModel, int, int>? Started;

    public async Task SweepAsync(bool includeAccountsWithAutomaticCheckDisabled = false)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            var accounts = _accounts.LoadAccounts()
                .Where(a => a.IsActive && a.BackendKind == BackendKind.Pop3Smtp
                    && (includeAccountsWithAutomaticCheckDisabled || a.CheckIncomingMail)).ToList();
            for (var index = 0; index < accounts.Count; index++)
            {
                var account = accounts[index];
                try
                {
                    Started?.Invoke(account, index + 1, accounts.Count);
                    var result = await _receiver.CheckAutomaticallyAsync(account, _stop.Token);
                    if (_rules is not null && result.NewMessages is { Count: > 0 })
                        await _rules.ApplyRulesAsync(result.NewMessages.ToList(), account.Id, _stop.Token);
                    Completed?.Invoke(account, result);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception ex) { LogService.Log($"POP3 receive failed for {account.AccountLabel}", ex); }
            }
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _timer.Dispose();
        _stop.Dispose();
    }
}
