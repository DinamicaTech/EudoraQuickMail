using System.Diagnostics;

namespace DinamicaBackup;

public static class ProfileLease
{
    public const string RequestName = ".backup-request.json";
    public const string ResponseName = ".backup-response.json";
    public static MaintenanceRequest? Request(string profile)
    {
        var path = Path.Combine(profile, RequestName);
        if (!File.Exists(path)) return null;
        var request = JsonFiles.Read<MaintenanceRequest>(path);
        return request.ExpiresUtc > DateTimeOffset.UtcNow ? request : null;
    }
    public static FileStream OpenApplication(string profile)
    {
        if (File.Exists(Path.Combine(profile, ".restore-in-progress.json")))
            throw new IOException("An interrupted restoration needs recovery in Dinamica Backup Agent before opening this profile.");
        if (Request(profile) != null) throw new IOException("The profile is entering its backup/maintenance window. Use Dinamica Backup Agent to cancel and reopen safely.");
        EnsureLock(profile);
        return new FileStream(Path.Combine(profile, ".backup-access.lock"), FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    private static void EnsureLock(string profile)
    {
        Directory.CreateDirectory(profile);
        var path = Path.Combine(profile, ".backup-access.lock");
        if (!File.Exists(path))
            using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
    }
    public static FileStream LockAgent(string profile)
    {
        Directory.CreateDirectory(profile);
        return new FileStream(Path.Combine(profile, ".backup-agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public static async Task<(FileStream Lease, MaintenanceResponse? Closed)> AcquireAsync(BackupJob job, IProgress<ProgressInfo>? progress, CancellationToken ct, Action<MaintenanceResponse>? onClosing = null, bool interactiveWatcherRequested = false)
    {
        EnsureLock(job.ProfileDirectory);
        var id = Guid.NewGuid().ToString("N");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(job.ShutdownTimeoutSeconds);
        var requestPath = Path.Combine(job.ProfileDirectory, RequestName);
        JsonFiles.Write(requestPath, new MaintenanceRequest(id, deadline, interactiveWatcherRequested));
        MaintenanceResponse? closed = null;
        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var responsePath = Path.Combine(job.ProfileDirectory, ResponseName);
                if (File.Exists(responsePath))
                {
                    var response = JsonFiles.Read<MaintenanceResponse>(responsePath);
                    if (response.Id == id) { closed = response; onClosing?.Invoke(response); }
                }
                if (closed != null && IsAlive(closed))
                { progress?.Report(new("Waiting for application process to exit")); await Task.Delay(250, ct); continue; }
                try
                {
                    var lease = new FileStream(Path.Combine(job.ProfileDirectory, ".backup-access.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    return (lease, closed);
                }
                catch (IOException) { }
                progress?.Report(new("Waiting for application to finish pending work and close"));
                await Task.Delay(500, ct);
            }
            throw new TimeoutException("Application did not close within the configured timeout. Nothing was forcibly terminated.");
        }
        finally { if (File.Exists(requestPath) && JsonFiles.Read<MaintenanceRequest>(requestPath).Id == id) File.Delete(requestPath); }
    }
    public static bool IsAlive(MaintenanceResponse response)
    {
        try { using var process = Process.GetProcessById(response.ProcessId); return process.StartTime.ToUniversalTime().Ticks == response.StartUtcTicks && !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
