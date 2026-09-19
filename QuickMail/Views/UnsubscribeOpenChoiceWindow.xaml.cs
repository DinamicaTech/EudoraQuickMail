using System.Windows;

namespace QuickMail.Views;

public enum UnsubscribeOpenChoice
{
    Cancel,
    Isolated,
    DefaultBrowser,
}

public partial class UnsubscribeOpenChoiceWindow : Window
{
    public UnsubscribeOpenChoice Choice { get; private set; } = UnsubscribeOpenChoice.Cancel;

    public UnsubscribeOpenChoiceWindow(Uri address)
    {
        InitializeComponent();
        AddressField.Text = address.AbsoluteUri;
    }

    private void OpenIsolated_Click(object sender, RoutedEventArgs e)
    {
        Choice = UnsubscribeOpenChoice.Isolated;
        DialogResult = true;
    }

    private void OpenDefaultBrowser_Click(object sender, RoutedEventArgs e)
    {
        Choice = UnsubscribeOpenChoice.DefaultBrowser;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = UnsubscribeOpenChoice.Cancel;
        DialogResult = false;
    }
}
