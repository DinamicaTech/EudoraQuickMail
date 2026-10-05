using System.Diagnostics;
using System.Windows;
using DinamicaBackup;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class MainWindow
{
    private bool _updatePreparing;

    private async Task InstallUpdateAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (_updatePreparing || _backupDraining || Application.Current is not App { Profile: { } profile } app)
            throw new InvalidOperationException("An update or backup is already in progress.");
        _updatePreparing = true;
        var paused = false;
        var exiting = false;
        try
        {
            await _vm.PrepareUpdateAsync(progress, ct);
            ct.ThrowIfCancellationRequested();
            if (ProfileLease.Request(profile.ProfileDir) != null)
                throw new InvalidOperationException("A backup has been requested. Install the update after the backup finishes.");
            Window confirmationOwner = Application.Current.Windows.OfType<UpdateDialog>()
                .FirstOrDefault(window => window.Owner == this && window.IsVisible) ?? (Window)this;
            if (MessageBox.Show(confirmationOwner, "The update is ready. Save open drafts, close QuickMail and install it now?",
                    "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            // Close other editors first: saving their state is not covered by compose draft saving.
            if (Application.Current.Windows.OfType<Window>().Any(window => window != this && window.IsVisible
                && window is not ComposeWindow && window is not MessageWindow && window is not UpdateDialog))
                throw new InvalidOperationException("Close the other settings/editor dialogs before installing the update.");
            // Do not replace an open backup agent, even if its UI appears idle.
            foreach (var process in Process.GetProcessesByName("DinamicaBackupAgent"))
            {
                using (process)
                {
                    string? path;
                    try { path = process.MainModule?.FileName; }
                    catch { throw new InvalidOperationException("Close Dinamica Backup Agent before updating."); }
                    if (path?.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase) == true)
                        throw new InvalidOperationException("Close Dinamica Backup Agent before updating. Wait for any active backup to finish.");
                }
            }
            paused = true;
            app.MailActivityPolicy?.SetMaintenance(true);
            foreach (var window in _openComposeWindows) window.IsEnabled = false;
            var elapsed = Stopwatch.StartNew();
            while (_vm.IsBusy || app.HasPendingBackupMailWork || !BackupOperationTracker.TryBeginMaintenance())
            {
                ct.ThrowIfCancellationRequested();
                if (elapsed.Elapsed > TimeSpan.FromMinutes(5))
                    throw new TimeoutException("Pending operations have not finished. The update was not installed.");
                progress.Report($"Waiting for pending mail operations… {elapsed.Elapsed:mm\\:ss} (Cancel to keep working)");
                await Task.Delay(250, ct);
            }
            progress.Report("Saving open drafts…");
            foreach (var tab in _vm.OpenTabs.OfType<ComposeTabViewModel>().ToArray())
                if (tab.PrepareForBackupAsync is null || !await tab.PrepareForBackupAsync())
                    throw new InvalidOperationException("An open draft could not be saved. The update was cancelled.");
            foreach (var window in _openComposeWindows.ToArray())
                if (!await window.PrepareForBackupAsync())
                    throw new InvalidOperationException("An open draft could not be saved. The update was cancelled.");
            ct.ThrowIfCancellationRequested();
            if (ProfileLease.Request(profile.ProfileDir) != null)
                throw new InvalidOperationException("A backup is about to start. Retry the update afterwards.");
            progress.Report("Closing QuickMail and installing the update…");
            if (!await _vm.RestartToUpdateAsync(ct)) throw new InvalidOperationException("The updater could not be started.");
            exiting = true;
            RequestExit();
        }
        finally
        {
            if (!exiting)
            {
                if (paused)
                {
                    BackupOperationTracker.SetMaintenance(false);
                    app.MailActivityPolicy?.SetMaintenance(false);
                    foreach (var window in _openComposeWindows) window.IsEnabled = true;
                }
                _updatePreparing = false;
            }
        }
    }
}
