using System.Diagnostics;
using System.IO;
using System.Windows;
using DinamicaBackup;

namespace DinamicaBackupAgent;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? Value(string key) { var i = Array.FindIndex(args, a => a.Equals(key, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var path = Value("/job");
        var watching = args.Contains("/watch", StringComparer.OrdinalIgnoreCase);
        if (!watching && string.IsNullOrWhiteSpace(path)) { MessageBox.Show("Use DinamicaBackupAgent.exe /config /job <backup-job.json>"); return 64; }
        try
        {
            if (watching)
                return ReopenWatcher.RunAsync(Value("/profile")!, Value("/application")!, Value("/request")!,
                    int.Parse(Value("/pid")!), long.Parse(Value("/started")!), CancellationToken.None).GetAwaiter().GetResult();
            var jobPath = path!;
            var job = File.Exists(jobPath) ? JsonFiles.Read<BackupJob>(jobPath)
                : new BackupJob { ProfileDirectory = Path.GetDirectoryName(Path.GetFullPath(jobPath))! };
            if (args.Contains("/scheduled", StringComparer.OrdinalIgnoreCase))
            {
                using var current = Process.GetCurrentProcess();
                if (Environment.UserInteractive && current.SessionId != 0 && HasDueSchedule(job, jobPath))
                {
                    var scheduledApp = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    var window = new ScheduledWindow(job, jobPath); scheduledApp.Run(window); return window.ExitCode;
                }
                return Scheduled(job, jobPath, CancellationToken.None).GetAwaiter().GetResult();
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new AgentWindow(job, jobPath)); return 0;
        }
        catch (Exception ex)
        {
            if (watching)
            {
                var profile = Value("/profile");
                if (!string.IsNullOrWhiteSpace(profile))
                    JsonFiles.Write(Path.Combine(profile, "backup-reopen-error.json"), new { AtUtc = DateTimeOffset.UtcNow, Error = ex.Message });
            }
            else if (args.Contains("/scheduled", StringComparer.OrdinalIgnoreCase))
                JsonFiles.Write(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path!))!, "backup-agent-last-error.json"), new { AtUtc = DateTimeOffset.UtcNow, Error = ex.Message });
            else MessageBox.Show(ex.Message, "Dinamica Backup Agent", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
    internal static Runner CreateRunner(BackupJob job, IProgress<ProgressInfo>? progress = null, bool interactive = true)
    {
        return new Runner(job, progress)
        {
            ReadCredential = Secrets.Read,
            ConnectDestination = () => NasConnection.ConnectDestinations(job),
            ReopenApplication = () =>
            {
                if (!interactive) throw new InvalidOperationException("Backup completed without an interactive session. Open the application manually at your next logon.");
                if (!File.Exists(job.ApplicationPath)) throw new FileNotFoundException("Application executable is missing.");
                var start = new ProcessStartInfo(job.ApplicationPath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(job.ApplicationPath)! };
                foreach (var arg in job.ApplicationArguments) start.ArgumentList.Add(arg);
                Process.Start(start);
            }
        };
    }
    private static string ScheduleStatePath(string jobPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jobPath))!, ".backup-schedule-state.json");
    private static Dictionary<string, DateTime> ReadScheduleState(string jobPath) => File.Exists(ScheduleStatePath(jobPath))
        ? JsonFiles.Read<Dictionary<string, DateTime>>(ScheduleStatePath(jobPath)) : [];
    private static bool HasDueSchedule(BackupJob job, string jobPath)
    {
        if (!job.BackupSchedule.Enabled && !job.MaintenanceSchedule.Enabled && !job.TestSchedule.Enabled) return false;
        var state = ReadScheduleState(jobPath);
        return new[] { ("Backup", job.BackupSchedule), ("Maintenance", job.MaintenanceSchedule), ("Test", job.TestSchedule) }.Any(pair =>
        {
            var at = pair.Item2.LatestOccurrence(DateTime.Now);
            return at.HasValue && (!state.TryGetValue(pair.Item1, out var prior) || prior < at.Value);
        });
    }
    internal static async Task<int> Scheduled(BackupJob job, string path, CancellationToken ct, IProgress<ProgressInfo>? progress = null)
    {
        // A local scheduler lease prevents overlapping invocations before a runner acquires its lease.
        using var scheduleLease = new FileStream(Path.Combine(Path.GetDirectoryName(path)!, ".backup-schedule.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Explicit requests are independent of schedule enablement/occurrence and consume once.
        if (await ManualWorker.ProcessAsync(job, path, ct)) return 0;
        SafePaths.Validate(job);
        var state = ReadScheduleState(path);
        var status = 0;
        foreach (var pair in new[] { ("Backup", job.BackupSchedule), ("Maintenance", job.MaintenanceSchedule), ("Test", job.TestSchedule) })
        {
            ct.ThrowIfCancellationRequested();
            var occurrence = pair.Item2.LatestOccurrence(DateTime.Now);
            if (occurrence == null) continue;
            {
                var hasPrevious = state.TryGetValue(pair.Item1, out var previous);
                if (hasPrevious && previous >= occurrence) continue;
                foreach (var missed in pair.Item2.Pending(DateTime.Now, hasPrevious ? previous : null).Where(x => x < occurrence.Value))
                    SaveMissed(job, new RunResult { Kind = pair.Item1, StartedUtc = new DateTimeOffset(missed), FinishedUtc = DateTimeOffset.UtcNow, Status = "Missed", Detail = "Scheduled execution was missed while the agent was unavailable." });
                var expiredMaintenance = pair.Item1 == "Maintenance" && occurrence.Value.AddMinutes(job.MaintenanceMinutes) < DateTime.Now;
                if (expiredMaintenance || (!pair.Item2.CatchUp && DateTime.Now - occurrence.Value > TimeSpan.FromMinutes(6)))
                {
                    state[pair.Item1] = occurrence.Value; JsonFiles.Write(ScheduleStatePath(path), state);
                    SaveMissed(job, new RunResult { Kind = pair.Item1, StartedUtc = new DateTimeOffset(occurrence.Value), FinishedUtc = DateTimeOffset.UtcNow, Status = "Missed", Detail = "Scheduled window was missed." }); continue;
                }
            }
            // Task Scheduler interactive-token tasks run in the user's desktop; session 0 does not.
            using var process = Process.GetCurrentProcess();
            var interactive = Environment.UserInteractive && process.SessionId != 0;
            var runner = CreateRunner(job, progress, interactive);
            runner.RequestInteractiveWatcher = !interactive;
            var result = await runner.RunAsync(pair.Item1, ct);
            state[pair.Item1] = occurrence.Value; JsonFiles.Write(ScheduleStatePath(path), state);
            if (DateTime.TryParse(result.CompletedMaintenanceOccurrence, out var maintenance))
            {
                state["Maintenance"] = maintenance; JsonFiles.Write(ScheduleStatePath(path), state);
            }
            progress?.Report(new(result.Status + ": " + result.Detail));
            if (result.Status == "Failed") status = 1;
        }
        return status;
    }
    private static void SaveMissed(BackupJob job, RunResult run)
    {
        var local = Path.Combine(job.ProfileDirectory, ".backup-history", job.Id, "run-" + run.Id + ".json");
        JsonFiles.Write(local, run);
        try
        {
            NasConnection.Connect(job.NasShare, job.NasCredential);
            using var catalog = new Catalog(job); catalog.Save(run);
        }
        catch (Exception) { /* Local receipt is retained until the destination becomes available. */ }
    }
}
