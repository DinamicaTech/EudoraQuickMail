using System.Windows;
using System.Windows.Controls;
using DinamicaBackup;

namespace DinamicaBackupAgent;

internal sealed class ScheduledWindow : Window
{
    public int ExitCode { get; private set; }
    public ScheduledWindow(BackupJob job, string path)
    {
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/DinamicaBackupAgent;component/Assets/DinamicaBackup.ico"));
        Title = "Dinamica Backup Agent — scheduled maintenance"; Width = 590; Height = 230; ResizeMode = ResizeMode.CanMinimize;
        var panel = new StackPanel { Margin = new Thickness(18) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = job.Name + " will close safely while its backup or maintenance runs.", TextWrapping = TextWrapping.Wrap });
        var text = new TextBlock { Margin = new Thickness(0, 15, 0, 10), TextWrapping = TextWrapping.Wrap }; panel.Children.Add(text);
        var bar = new ProgressBar { Height = 12, IsIndeterminate = true }; panel.Children.Add(bar);
        var button = new Button { Content = "Cancel safely / Reopen application", Margin = new Thickness(0, 15, 0, 0), Padding = new Thickness(8) }; panel.Children.Add(button);
        var cancellation = new CancellationTokenSource(); var finished = false;
        button.Click += (_, _) => { if (finished) Close(); else { cancellation.Cancel(); text.Text = "Cancelling safely…"; } };
        Closing += (_, e) => { if (!finished) { e.Cancel = true; cancellation.Cancel(); text.Text = "Cancelling safely before closing…"; } };
        Loaded += async (_, _) =>
        {
            var progress = new Progress<ProgressInfo>(p => { text.Text = p.Stage; bar.IsIndeterminate = p.Total == 0; bar.Maximum = Math.Max(1, p.Total); bar.Value = p.Completed; });
            try { ExitCode = await Task.Run(() => Program.Scheduled(job, path, cancellation.Token, progress)); }
            catch (OperationCanceledException) { text.Text = "Cancelled."; }
            catch (Exception ex) { text.Text = "Failed: " + ex.Message; ExitCode = 1; }
            finally { finished = true; cancellation.Dispose(); bar.IsIndeterminate = false; button.Content = "Close"; }
            if (ExitCode == 0) Close();
        };
    }
}
