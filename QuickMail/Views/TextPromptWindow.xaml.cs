using System.Windows;

namespace QuickMail.Views;

public partial class TextPromptWindow : Window
{
    public string Value => ValueBox.Text;

    public TextPromptWindow(string title, string prompt, string defaultValue)
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Text = prompt;
        ValueBox.Text = defaultValue;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueBox.Text)) return;
        DialogResult = true;
    }
}
