using System.Windows.Input;

namespace QuickMail.Helpers;

public static class QuickSearchKeyboard
{
    public static Task ExecuteEnterAsync(KeyEventArgs e, ModifierKeys modifiers, Func<bool, Task> search)
    {
        // Routed input must be consumed synchronously: awaiting the search first lets
        // ComboBox/default-button handlers process the same Enter a second time.
        e.Handled = true;
        if (e.IsRepeat) return Task.CompletedTask;
        var everywhere = (modifiers & ModifierKeys.Control) != 0;
        return search(everywhere);
    }
}
