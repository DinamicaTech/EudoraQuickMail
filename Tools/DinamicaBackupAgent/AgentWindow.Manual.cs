using System.Diagnostics;
using System.IO;
using DinamicaBackup;

namespace DinamicaBackupAgent;

public sealed partial class AgentWindow
{
    private async void StartManual(string kind)
    {
        if (_operation != null) return;
        ManualCommands? store = null;
        ManualCommand? request = null;
        try
        {
            EnsureSupportedApplication(); Save();
            if (kind != "Backup")
            {
                var destination = kind == "TestAttachmentAccess" ? _job.AttachmentDestination : _job.Destination;
                if (ApplicationDetection.UncShare(destination).Length == 0)
                    throw new ArgumentException("Select a UNC destination such as \\\\nas\\backup\\folder first.");
            }
            // No duplicate manual operation while a scheduled/local run owns the profile.
            using (ProfileLease.LockAgent(_job.ProfileDirectory)) { }
            store = new ManualCommands(_jobPath, _job.Id);
            store.RecoverAbandoned();
            _operation = new CancellationTokenSource();
            _tabs.IsEnabled = false; _actions.IsEnabled = false; _cancel.IsEnabled = true; _progress.IsIndeterminate = true;
            var queue = store;
            await Task.Run(() => WindowsScheduler.RunManual(_jobPath, _job.Id,
                () => request = queue.Submit(kind, ManualCommands.Hash(_jobPath))));
            _status.Text = "Request sent to Windows Task Scheduler. Waiting for acceptance…";
            var cancelSent = false;
            while (true)
            {
                await Task.Delay(300); // cancellation requests stop the worker, not result/cleanup monitoring
                try
                {
                    if (_operation.IsCancellationRequested && !cancelSent)
                    { store.Cancel(request!.Id); cancelSent = true; }
                    store.RecoverAbandoned();
                    var status = store.ReadStatus(request!.Id);
                    if (status?.Id != request.Id) throw new InvalidOperationException("Manual request status is unavailable.");
                    _status.Text = (cancelSent && !status.Finished ? "Cancelling safely…\n" : "") + status.Detail;
                    _progress.IsIndeterminate = !status.Finished && status.Total == 0;
                    _progress.Maximum = Math.Max(1, status.Total); _progress.Value = status.Completed;
                    if (!status.Finished) continue;
                    if (status.ReopenApplication)
                    {
                        try
                        {
                            var start = new ProcessStartInfo(_job.ApplicationPath) { UseShellExecute = true,
                                WorkingDirectory = Path.GetDirectoryName(_job.ApplicationPath)! };
                            foreach (var argument in _job.ApplicationArguments) start.ArgumentList.Add(argument);
                            Process.Start(start);
                        }
                        catch (Exception ex) { _status.Text += "\nCould not reopen application: " + ex.Message; }
                    }
                    break;
                }
                catch (IOException) { /* writer briefly holds the status lock; retry */ }
            }
        }
        catch (Exception ex)
        {
            if (request != null && store != null)
            {
                try { store.Cancel(request.Id); } catch (IOException) { }
            }
            _status.Text = "Request failed: " + ex.Message;
        }
        finally
        {
            _operation?.Dispose(); _operation = null; _tabs.IsEnabled = true; _actions.IsEnabled = true;
            _cancel.IsEnabled = false; _progress.IsIndeterminate = false;
        }
    }
}
