using System.Diagnostics;
using System.IO;
using DinamicaBackup;

namespace DinamicaBackupAgent;

internal static class ReopenWatcher
{
    internal static async Task<int> RunAsync(string profile, string application, string requestId,
        int originalPid, long originalStarted, CancellationToken ct)
    {
        using var self = Process.GetCurrentProcess();
        if (!Environment.UserInteractive || self.SessionId == 0)
            throw new InvalidOperationException("The reopen watcher must run in the user's interactive session.");
        if (!Guid.TryParseExact(requestId, "N", out _) || string.IsNullOrWhiteSpace(profile)
            || !File.Exists(application) || originalPid <= 0 || originalStarted <= 0)
            throw new ArgumentException("Invalid backup reopen watcher arguments.");

        var responsePath = Path.Combine(profile, ProfileLease.ResponseName);
        var deadline = DateTimeOffset.UtcNow.AddHours(24);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            MaintenanceResponse? response = null;
            try
            {
                if (File.Exists(responsePath)) response = JsonFiles.Read<MaintenanceResponse>(responsePath);
            }
            catch (IOException) { } // Atomic replacement may briefly make the response unreadable.
            if (response is not { InteractiveWatcherStarted: true } || response.Id != requestId
                || response.ProcessId != originalPid || response.StartUtcTicks != originalStarted)
            {
                if (ProfileLease.Request(profile)?.Id != requestId) return 2; // Shutdown was cancelled or timed out.
                await Task.Delay(500, ct);
                continue;
            }
            if (ProfileLease.IsAlive(response))
            {
                if (ProfileLease.Request(profile)?.Id != requestId) return 2;
                await Task.Delay(500, ct);
                continue;
            }

            // The agent owns this lock for the entire run, including notifications and status writes.
            // A manually started EQM is already waiting and owns responsibility for reopening.
            if (StartupCancellation.HasLiveWaiter(profile)) return 0;
            // Holding it while launching EQM prevents the next scheduled run from racing the reopen.
            try
            {
                using var agent = ProfileLease.LockAgent(profile);
                using (ProfileLease.OpenApplication(profile)) { }
                if (StartupCancellation.HasLiveWaiter(profile)) return 0;
                var start = new ProcessStartInfo(application)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(application)!
                };
                start.ArgumentList.Add("--profileDir");
                start.ArgumentList.Add(profile);
                using var reopened = Process.Start(start) ?? throw new IOException("Could not reopen QuickMail.");
                return 0;
            }
            catch (IOException)
            {
                await Task.Delay(500, ct);
            }
        }
        throw new TimeoutException("The interactive reopen watcher timed out waiting for the backup to release the profile.");
    }
}
