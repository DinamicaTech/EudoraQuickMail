using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DinamicaBackup;

namespace DinamicaBackupAgent;

internal static class ManualWorker
{
    public static async Task<bool> ProcessAsync(BackupJob scheduledJob, string jobPath, CancellationToken ct)
    {
        var store = new ManualCommands(jobPath, scheduledJob.Id);
        store.RecoverAbandoned();
        var request = store.Claim();
        if (request == null) return false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watch = WatchAsync(store, request.Id, cancellation);
        var reopen = false;
        try
        {
            if (store.CancellationRequested(request.Id)) cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            // Read one snapshot; never apply a manual request to a subsequently edited configuration.
            var bytes = File.ReadAllBytes(jobPath);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != request.ConfigurationHash)
                throw new InvalidOperationException("The configuration changed after this request. Submit the operation again.");
            var job = JsonSerializer.Deserialize<BackupJob>(bytes, JsonFiles.Options) ?? throw new InvalidDataException("Invalid job configuration.");
            if (job.Id != request.JobId) throw new InvalidDataException("The request belongs to another job.");
            RunResult? result = null;
            string detail;
            if (request.Kind == "Backup")
            {
                var runner = Program.CreateRunner(job, new CommandProgress(store, request.Id), interactive: false);
                runner.ReopenApplication = () => reopen = true; // the waiting desktop UI reopens it, not session 0
                result = await runner.RunAsync("Backup", cancellation.Token);
                detail = result.Status + ": " + result.Detail;
                if (result.Warnings.Count > 0) detail += "\n" + string.Join("\n", result.Warnings);
                if (result.NotificationError.Length > 0) detail += "\nReport: " + result.NotificationError;
            }
            else if (request.Kind is "TestDatabaseAccess" or "TestAttachmentAccess")
            {
                var attachment = request.Kind == "TestAttachmentAccess";
                var destination = attachment ? job.AttachmentDestination : job.Destination;
                var share = attachment ? job.AttachmentNasShare : job.NasShare;
                var credential = attachment ? job.AttachmentNasCredential : job.NasCredential;
                var root = ApplicationDetection.UncShare(destination);
                if (root.Length == 0) throw new ArgumentException("Select a UNC destination (\\\\server\\share\\folder), not a mapped drive.");
                if (credential.Length > 0 && !root.Equals(share.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The credential's UNC share does not match the selected destination.");
                using var operation = ProfileLease.LockAgent(job.ProfileDirectory);
                store.Update(request.Id, "Running", "Connecting to UNC destination with the Windows task identity…");
                cancellation.Token.ThrowIfCancellationRequested();
                NasConnection.Connect(share, credential);
                cancellation.Token.ThrowIfCancellationRequested();
                store.Update(request.Id, "Running", "Checking folder listing, file creation, read-back and deletion…");
                DirectoryAccessProbe.Test(destination, cancellation.Token);
                detail = "Access OK: listing, creation, reading and deletion verified.\n" + destination
                    + "\nWindows task identity: " + System.Security.Principal.WindowsIdentity.GetCurrent().Name
                    + "\nNAS user: " + (credential.Length == 0 ? "Windows identity" : Secrets.Read(credential).User);
            }
            else throw new InvalidDataException("Unsupported manual request.");
            cancellation.Cancel(); await watch;
            var state = result?.Status switch { "Failed" => "Failed", "Cancelled" => "Cancelled", _ => "Completed" };
            store.Update(request.Id, state, detail, reopen: reopen, result: result);
        }
        catch (OperationCanceledException)
        {
            cancellation.Cancel(); await watch;
            store.Update(request.Id, "Cancelled", "Operation cancelled safely.", reopen: reopen);
        }
        catch (Exception ex)
        {
            cancellation.Cancel(); await watch;
            store.Update(request.Id, "Failed", ex.Message, reopen: reopen);
        }
        return true;
    }
    private static async Task WatchAsync(ManualCommands store, string id, CancellationTokenSource cancellation)
    {
        try
        {
            while (true)
            {
                await Task.Delay(300, cancellation.Token);
                try { if (store.CancellationRequested(id)) { cancellation.Cancel(); return; } }
                catch (IOException) { } // atomic rename may temporarily race a reader on Windows
            }
        }
        catch (OperationCanceledException) { }
    }
    private sealed class CommandProgress(ManualCommands store, string id) : IProgress<ProgressInfo>
    {
        private readonly object _gate = new();
        private DateTime _last;
        public void Report(ProgressInfo value)
        {
            lock (_gate)
            {
                if (DateTime.UtcNow - _last < TimeSpan.FromMilliseconds(300)) return;
                _last = DateTime.UtcNow;
                try { store.Update(id, "Running", value.Stage, value.Completed, value.Total); }
                catch (IOException) { } // a status poll must not fail the backup
            }
        }
    }
}
