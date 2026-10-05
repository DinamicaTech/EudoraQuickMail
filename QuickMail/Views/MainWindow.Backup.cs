using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DinamicaBackup;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Views;

public partial class MainWindow
{
    private DispatcherTimer? _backupPollTimer;
    private bool _backupPollRunning;
    private bool _backupDraining;
    internal void InitializeBackupCoordination()
    {
        _backupPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _backupPollTimer.Tick += async (_, _) => await PollBackupRequestAsync();
        _backupPollTimer.Start();
    }
    private async Task PollBackupRequestAsync()
    {
        if (_updatePreparing || _backupPollRunning || Application.Current is not App { Profile: { } profile } app || app.IsShutdownInProgress) return;
        _backupPollRunning = true;
        try
        {
            var request = ProfileLease.Request(profile.ProfileDir);
            if (request == null)
            {
                if (_backupDraining)
                {
                    _backupDraining = false; IsEnabled = true;
                    foreach (var window in _openComposeWindows) window.IsEnabled = true;
                    BackupOperationTracker.SetMaintenance(false); app.MailActivityPolicy?.SetMaintenance(false);
                    _vm.StatusText = "Backup request ended; application is available again.";
                }
                return;
            }
            _backupDraining = true; IsEnabled = false;
            foreach (var window in _openComposeWindows) window.IsEnabled = false;
            app.MailActivityPolicy?.SetMaintenance(true);
            _vm.StatusText = "Preparing backup: waiting for pending work and saving drafts…";
            if (_vm.IsBusy || app.HasPendingBackupMailWork) return;
            if (Application.Current.Windows.OfType<Window>().Any(window => window != this
                && window is not ComposeWindow && window is not MessageWindow && window.IsVisible))
            {
                _vm.StatusText = "Backup is waiting for an open settings/editor dialog to close.";
                return;
            }
            foreach (var tab in _vm.OpenTabs.OfType<ComposeTabViewModel>().ToArray())
                if (tab.PrepareForBackupAsync is null || !await tab.PrepareForBackupAsync()) return;
            foreach (var window in _openComposeWindows.ToArray()) if (!await window.PrepareForBackupAsync()) return;
            if (_vm.IsBusy || app.HasPendingBackupMailWork || ProfileLease.Request(profile.ProfileDir)?.Id != request.Id) return;
            if (!BackupOperationTracker.TryBeginMaintenance()) return;
            using var process = Process.GetCurrentProcess();
            var watcherStarted = false;
            if (request.InteractiveWatcherRequested)
            {
                var watcherPath = Path.Combine(AppContext.BaseDirectory, "BackupAgent", "DinamicaBackupAgent.exe");
                var applicationPath = Environment.ProcessPath;
                if (!File.Exists(watcherPath) || string.IsNullOrWhiteSpace(applicationPath))
                    throw new FileNotFoundException("The interactive backup reopen watcher is not installed.", watcherPath);
                var start = new ProcessStartInfo(watcherPath)
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = AppContext.BaseDirectory
                };
                foreach (var argument in new[] { "/watch", "/profile", profile.ProfileDir, "/application", applicationPath,
                    "/request", request.Id, "/pid", process.Id.ToString(), "/started", process.StartTime.ToUniversalTime().Ticks.ToString() })
                    start.ArgumentList.Add(argument);
                using var watcher = Process.Start(start) ?? throw new IOException("Could not start the interactive backup reopen watcher.");
                if (watcher.WaitForExit(200)) throw new IOException("The interactive backup reopen watcher stopped unexpectedly.");
                watcherStarted = true;
            }
            JsonFiles.Write(Path.Combine(profile.ProfileDir, ProfileLease.ResponseName),
                new MaintenanceResponse(request.Id, process.Id, process.StartTime.ToUniversalTime().Ticks, watcherStarted));
            LogService.Log("Backup: pending mail operations drained, drafts saved; closing application for agent.");
            RequestExit();
        }
        catch (Exception ex)
        {
            BackupOperationTracker.SetMaintenance(false);
            LogService.Log("Backup shutdown coordination", ex);
            _vm.StatusText = "Backup preparation: " + ex.Message;
        }
        finally { _backupPollRunning = false; }
    }
    private void MenuBackupRestore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Application.Current is not App { Profile: { } profile }) return;
            var executable = Path.Combine(AppContext.BaseDirectory, "BackupAgent", "DinamicaBackupAgent.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Dinamica Backup Agent is not installed. Install a package containing the BackupAgent folder.");
            var path = Path.Combine(profile.ProfileDir, "backup-job.json");
            if (!File.Exists(path))
            {
                JsonFiles.Write(path, new BackupJob
                {
                    ProfileDirectory = profile.ProfileDir,
                    ApplicationPath = Environment.ProcessPath ?? "",
                    ApplicationArguments = ["--profileDir", profile.ProfileDir],
                    ProfileFiles = ["accounts.json", "config.ini", "hotkeys.json", "rules.json", "views.json", "contacts.json", "groups.json", "templates.json", "scheduled-mail.json", "snooze.json", "flags.json", "mail-activity.json", "eudora-import-settings.json"]
                });
            }
            var start = new ProcessStartInfo(executable) { UseShellExecute = true };
            start.ArgumentList.Add("/config"); start.ArgumentList.Add("/job"); start.ArgumentList.Add(path);
            Process.Start(start);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Backup and Restore", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
