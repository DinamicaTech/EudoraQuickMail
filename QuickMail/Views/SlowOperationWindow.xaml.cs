using System.Windows;
using System.Windows.Threading;

namespace QuickMail.Views;

public partial class SlowOperationWindow : Window
{
    private readonly Func<string>? _statusProvider;
    private readonly DispatcherTimer _timer;
    private readonly Action? _cancel;

    public SlowOperationWindow(string title, Func<string>? statusProvider = null, Action? cancel = null)
    {
        InitializeComponent();
        Title = title;
        OperationTitle.Text = title;
        _statusProvider = statusProvider;
        _cancel = cancel;
        CancelOperation.Visibility = cancel is null ? Visibility.Collapsed : Visibility.Visible;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => RefreshStatus(), Dispatcher);
        Closed += (_, _) => _timer.Stop();
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        CancelOperation.IsEnabled = false;
        _cancel?.Invoke();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        RefreshStatus();
        _timer.Start();
    }

    private void RefreshStatus()
    {
        var status = _statusProvider?.Invoke();
        OperationStatus.Text = string.IsNullOrWhiteSpace(status) ? "Working…" : status;
    }
}
