using System.Windows;
using QuickMail.Models;

namespace QuickMail.Views;

public partial class AccountHealthWindow : Window
{
    private readonly Func<Task<IReadOnlyList<AccountHealthRow>>> _loader;

    public AccountHealthWindow(Func<Task<IReadOnlyList<AccountHealthRow>>> loader)
    {
        InitializeComponent();
        _loader = loader;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void HealthGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // GridView has no star-sized columns. Keep the complete diagnostic row inside the
        // available viewport and distribute space according to the expected content length.
        var available = HealthGrid.ActualWidth - SystemParameters.VerticalScrollBarWidth - 12;
        if (available < 760) return;

        AccountColumn.Width        = available * 0.12;
        StateColumn.Width          = available * 0.09;
        ProtocolColumn.Width       = available * 0.09;
        IncomingColumn.Width       = available * 0.13;
        OutgoingColumn.Width       = available * 0.13;
        AuthenticationColumn.Width = available * 0.15;
        LastDownloadColumn.Width   = available * 0.11;
        QueueColumn.Width          = available * 0.08;
        LastErrorColumn.Width      = available * 0.10;
    }

    private async Task RefreshAsync()
    {
        try
        {
            IsEnabled = false;
            StatusText.Text = "Checking configuration, credentials and outgoing queue…";
            var rows = await _loader();
            HealthGrid.ItemsSource = rows;
            StatusText.Text = $"{rows.Count:N0} account{(rows.Count == 1 ? "" : "s")} checked.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Account health check failed: {ex.Message}";
        }
        finally
        {
            IsEnabled = true;
        }
    }
}
