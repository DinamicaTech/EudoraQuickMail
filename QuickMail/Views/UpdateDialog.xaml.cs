using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;

namespace QuickMail.Views;

/// <summary>
/// Download progress and explicit installation for both installed and portable copies.
/// Dismissing the dialog cancels preparation; it never schedules a surprise update on exit.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "_restartCts is cancelled and disposed in the Closed handler; WPF never calls Dispose on Window instances, so implementing IDisposable here would be dead code.")]
public partial class UpdateDialog : Window
{
    private readonly Func<IProgress<string>, CancellationToken, Task> _restartToUpdate;
    private readonly CancellationTokenSource _restartCts = new();

    public UpdateDialog(string version, string whatsNewUrl, Func<IProgress<string>, CancellationToken, Task> restartToUpdate)
    {
        _restartToUpdate = restartToUpdate;
        InitializeComponent();
        MessageText.Text =
            $"Version {version} of Eudora QuickMail is available. Download the complete update here. " +
            "You will be asked before QuickMail closes. Mail and settings will be preserved.";
        WhatsNewLink.NavigateUri = new Uri(whatsNewUrl);
        Loaded += (_, _) => RestartButton.Focus();
        Closed += (_, _) => { _restartCts.Cancel(); _restartCts.Dispose(); };
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        RestartButton.IsEnabled = false;
        var ct = _restartCts.Token;
        try
        {
            await _restartToUpdate(new Progress<string>(text => { if (IsVisible) ProgressText.Text = text; }), ct);
            if (IsVisible) Close();
        }
        catch (OperationCanceledException) { if (IsVisible) Close(); }
        catch (Exception ex)
        {
            Services.LogService.Log("In-app update", ex);
            if (IsVisible) { ProgressText.Text = "Update not installed: " + ex.Message; RestartButton.IsEnabled = true; }
        }
    }

    private void WhatsNewLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // All ShellExecute launches go through the allow-list. See ExternalUriPolicy.
        Helpers.ExternalUriPolicy.TryOpenExternal(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}
