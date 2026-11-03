using System.IO;
using QuickMail.Helpers;

namespace QuickMail.Services;

public sealed record AccountHealthObservation(Guid AccountId, string Channel,
    DateTimeOffset AttemptUtc, DateTimeOffset? SuccessUtc, string? Error);

/// <summary>Local evidence, not a promise that a server is currently reachable.</summary>
public sealed class AccountHealthJournal(string directory)
{
    public static AccountHealthJournal? Current { get; set; }
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(directory, "account-health.json");
    public IReadOnlyList<AccountHealthObservation> Read()
    {
        lock (_gate) return ProtectedJsonFile.Read<List<AccountHealthObservation>>(_path, () => []);
    }
    public void Record(Guid account, string channel, Exception? error = null)
    {
        if (error is OperationCanceledException) return;
        lock (_gate)
        {
            var rows = Read().ToList();
            var old = rows.FirstOrDefault(x => x.AccountId == account && x.Channel == channel);
            rows.RemoveAll(x => x.AccountId == account && x.Channel == channel);
            var now = DateTimeOffset.UtcNow;
            // Never persist exception messages: providers may include OAuth response bodies.
            rows.Add(new(account, channel, now, error == null ? now : old?.SuccessUtc,
                error == null ? null : $"{error.GetType().Name} (0x{error.HResult:X8}). " +
                    (channel == "Calendar" ? "See diagnostic log / calendar account settings." : "See diagnostic log / Test Connection.")));
            ProtectedJsonFile.Write(_path, rows.OrderByDescending(x => x.AttemptUtc).Take(512).ToList());
        }
    }
    public static void Observe(Guid account, string channel, Exception? error = null)
    {
        try { Current?.Record(account, channel, error); }
        catch (Exception ex) { LogService.Log("Could not persist account health", ex); }
    }
}
