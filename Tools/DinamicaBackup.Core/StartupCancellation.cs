using System.Diagnostics;
using System.Text.Json;

namespace DinamicaBackup;

public sealed record ActiveBackup(string Id, string Kind, int ProcessId, long StartUtcTicks);
public sealed record BackupCancelRequest(string Id);

/// <summary>Profile-local cooperative cancellation, scoped to one live agent run.</summary>
public sealed class StartupCancellation : IDisposable
{
    public const string ActiveName = ".backup-active.json";
    public const string CancelName = ".backup-cancel.json";
    public const string WaiterName = ".backup-startup-waiter.json";
    private readonly string _profile;
    private readonly ActiveBackup _active;
    private readonly CancellationTokenSource _source;
    private readonly Task _watch;
    public CancellationToken Token => _source.Token;
    public bool Requested { get; private set; }

    // Caller owns LockAgent throughout this lifetime.
    public StartupCancellation(string profile, string id, string kind, CancellationToken token)
    {
        _profile = profile;
        using var process = Process.GetCurrentProcess();
        _active = new(id, kind, process.Id, process.StartTime.ToUniversalTime().Ticks);
        _source = CancellationTokenSource.CreateLinkedTokenSource(token);
        JsonFiles.Write(Path.Combine(profile, ActiveName), _active);
        _watch = Task.Run(async () =>
        {
            try
            {
                while (!_source.IsCancellationRequested)
                {
                    if (Read<BackupCancelRequest>(Path.Combine(profile, CancelName))?.Id == id && kind != "Restore")
                    {
                        Requested = true;
                        _source.Cancel();
                        return;
                    }
                    await Task.Delay(250, _source.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public static ActiveBackup? Active(string profile)
    {
        var active = Read<ActiveBackup>(Path.Combine(profile, ActiveName));
        if (active == null) return null;
        try
        {
            return ProfileLease.IsAlive(new(active.Id, active.ProcessId, active.StartUtcTicks)) ? active : null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            // A user-session UI may not query the start time of a SYSTEM worker. Cancellation
            // is still safe: only the worker holding the matching random run ID accepts it,
            // and startup continues only after acquiring the actual locks.
            return active;
        }
        catch (InvalidOperationException) { return null; }
    }

    public void ReportExternalWindow() => JsonFiles.Write(Path.Combine(_profile, ActiveName), _active with { Kind = "Maintenance" });

    public static bool Request(string profile, string id)
    {
        var active = Active(profile);
        if (active == null || active.Id != id || active.Kind == "Restore") return false;
        JsonFiles.Write(Path.Combine(profile, CancelName), new BackupCancelRequest(id));
        return true;
    }

    public static bool WasRequested(string profile, string id) =>
        Read<BackupCancelRequest>(Path.Combine(profile, CancelName))?.Id == id;

    public static void RegisterWaiter(string profile)
    {
        using var process = Process.GetCurrentProcess();
        JsonFiles.Write(Path.Combine(profile, WaiterName),
            new MaintenanceResponse("startup", process.Id, process.StartTime.ToUniversalTime().Ticks));
    }

    public static bool HasLiveWaiter(string profile) =>
        Read<MaintenanceResponse>(Path.Combine(profile, WaiterName)) is { } waiter && ProfileLease.IsAlive(waiter);

    public static FileStream OpenForStartup(string profile)
    {
        using var agent = ProfileLease.LockAgent(profile);
        return ProfileLease.OpenApplication(profile);
    }

    public static void RemoveWaiter(string profile)
    {
        var path = Path.Combine(profile, WaiterName);
        if (Read<MaintenanceResponse>(path)?.ProcessId == Environment.ProcessId) File.Delete(path);
    }

    private static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonFiles.Read<T>(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    public void Dispose()
    {
        _source.Cancel();
        _watch.GetAwaiter().GetResult();
        _source.Dispose();
        try
        {
            var path = Path.Combine(_profile, ActiveName);
            if (Read<ActiveBackup>(path)?.Id == _active.Id) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
