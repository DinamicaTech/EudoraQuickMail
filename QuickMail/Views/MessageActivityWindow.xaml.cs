using System.Windows;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Views;

public partial class MessageActivityWindow : Window
{
    private readonly CommandRegistry _commands = new();
    private readonly IInputElement? _origin = Keyboard.FocusedElement;
    public MessageActivityWindow(string subject, string text, string? reportTitle = null)
    {
        InitializeComponent();
        Title = reportTitle ?? "Activity — " + subject;
        System.Windows.Automation.AutomationProperties.SetName(Report, reportTitle ?? "Message activity");
        Report.Text = text;
        _commands.Register(new CommandDefinition("activity.close", "Mail", "Close report", Close, Key.Escape));
        Loaded += (_, _) => Report.Focus();
        Closed += (_, _) => { if (_origin != null) Keyboard.Focus(_origin); };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F6)
        {
            if (Report.IsKeyboardFocusWithin) CloseButton.Focus(); else Report.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            new CommandPaletteWindow(_commands) { Owner = this }.ShowDialog();
            e.Handled = true;
        }
        else if (_commands.FindByGesture(e.Key, Keyboard.Modifiers) is { } command)
        { command.Execute(); e.Handled = true; }
    }
}
