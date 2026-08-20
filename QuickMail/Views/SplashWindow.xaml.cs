using System.Windows;
using System.Windows.Threading;

namespace QuickMail.Views;

public partial class SplashWindow : Window
{
    public SplashWindow() => InitializeComponent();

    public void SetStatus(string status)
    {
        StatusText.Text = status;
        Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
    }
}
