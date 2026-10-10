using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DinamicaBackup;

namespace QuickMail.Views;

/// <summary>Waits without touching SQLite; only continues after both agent and profile leases permit it.</summary>
internal sealed class BackupStartupWindow : Window
{
    private readonly string _profile;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button _cancel = new() { Content = "Cancel backup and open EQM", Padding = new Thickness(12, 6, 12, 6) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string? _requested;
    public FileStream? Lease { get; private set; }

    public BackupStartupWindow(string profile)
    {
        _profile = profile;
        Title = "Eudora QuickMail — backup in progress";
        Width = 590; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = "EQM is waiting for the backup agent to release this profile.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_status);
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 10, Margin = new Thickness(0, 0, 0, 16) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_cancel);
        var close = new Button { Content = "Close (leave backup running)", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        buttons.Children.Add(close); panel.Children.Add(buttons);
        close.Click += (_, _) => Close();
        _cancel.Click += (_, _) => RequestCancellation();
        _timer.Tick += (_, _) => Check();
        Loaded += (_, _) =>
        {
            try { StartupCancellation.RegisterWaiter(_profile); Check(); if (IsVisible) _timer.Start(); }
            catch (Exception ex) { _status.Text = ex.Message; _cancel.IsEnabled = false; }
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            // Keep the live process identity after successful handoff so the watcher exits
            // instead of later launching EQM again when this application is closed.
            try { if (Lease == null) StartupCancellation.RemoveWaiter(_profile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }

    private void RequestCancellation()
    {
        try
        {
            var active = StartupCancellation.Active(_profile);
            if (active == null || active.Kind == "Restore") { Check(); return; }
            if (active.Kind == "Maintenance" && MessageBox.Show(this,
                "This only ends EQM's external backup window. It cannot stop Synology or another external backup.\n\n" +
                "Confirm that the external backup has finished or has been stopped before continuing.",
                "External backup window", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            if (StartupCancellation.Request(_profile, active.Id)) _requested = active.Id;
            Check();
        }
        catch (Exception ex) { _status.Text = "Could not request cancellation: " + ex.Message; }
    }

    private void Check()
    {
        try
        {
            // Do not use file existence as evidence of a lock. Never remove either lock file.
            Lease = StartupCancellation.OpenForStartup(_profile);
            DialogResult = true;
            return;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException ex) { _status.Text = ex.Message; _cancel.IsEnabled = false; return; }

        var active = StartupCancellation.Active(_profile);
        _cancel.IsEnabled = active != null && active.Kind != "Restore" && active.Id != _requested;
        _status.Text = File.Exists(Path.Combine(_profile, ".restore-in-progress.json"))
            ? "An interrupted restore requires recovery in Dinamica Backup Agent. EQM cannot open this profile yet."
            : active == null
                ? "The profile is locked, but no compatible active agent was detected. An older agent cannot receive this cancellation request. Waiting for release; no process will be forcibly stopped."
                : active.Id == _requested
                    ? "Cancellation requested. Waiting for the current operation and cleanup to release the profile…"
                    : active.Kind == "Restore"
                        ? "A restore is in progress. Wait for it to complete or manage it in Dinamica Backup Agent."
                        : "Current operation: " + (active.Kind == "Maintenance" ? "External backup window" : active.Kind) + ". You can wait or request safe cancellation.";
    }
}
