using System.Windows;

namespace QuickMail.Views;

public partial class RuleFilterSearchDialog : Window
{
    public RuleFilterSearchDialog(string? from, string? to, string? subject, string? body)
    {
        InitializeComponent();
        FromBox.Text = from ?? string.Empty;
        ToBox.Text = to ?? string.Empty;
        SubjectBox.Text = subject ?? string.Empty;
        BodyBox.Text = body ?? string.Empty;
        Loaded += (_, _) => FromBox.Focus();
    }

    public string FromCriteria => FromBox.Text;
    public string ToCriteria => ToBox.Text;
    public string SubjectCriteria => SubjectBox.Text;
    public string BodyCriteria => BodyBox.Text;

    private void Search_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        FromBox.Clear(); ToBox.Clear(); SubjectBox.Clear(); BodyBox.Clear();
        DialogResult = true;
        Close();
    }
}
