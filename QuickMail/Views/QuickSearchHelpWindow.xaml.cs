using System.Windows;

namespace QuickMail.Views;

public partial class QuickSearchHelpWindow : Window
{
    public QuickSearchHelpWindow()
    {
        InitializeComponent();
        DataContext = new[]
        {
            new Row("(none)", "From, To, Cc, Subject and message body", "chocolate"),
            new Row("T", "To recipients", "T:person@example.com"),
            new Row("F", "From sender", "F:@paypal.es"),
            new Row("F", "Messages with any flag (no operator or value)", "F"),
            new Row("C", "Cc recipients", "C:accounts"),
            new Row("S", "Subject", "S:quarterly report"),
            new Row("B", "Message body", "B:chocolate"),
            new Row("A#", "Number of attachments", "A#>=1"),
            new Row("AN", "Attachment name or internal path", "AN:invoice?.pdf"),
            new Row("AC", "Extracted attachment content", "AC:contract"),
            new Row("D", "Message date", "D=03/2015"),
            new Row("N", "Unread messages (no operator or value)", "N"),
        };
    }
    private sealed record Row(string Prefix, string Description, string Example);
}
