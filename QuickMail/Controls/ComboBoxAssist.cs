using System.Windows;

namespace QuickMail.Controls;

/// <summary>Optional presentation settings for the shared ComboBox template.</summary>
public static class ComboBoxAssist
{
    public static readonly DependencyProperty ShowDropDownButtonProperty =
        DependencyProperty.RegisterAttached(
            "ShowDropDownButton",
            typeof(bool),
            typeof(ComboBoxAssist),
            new FrameworkPropertyMetadata(true));

    public static bool GetShowDropDownButton(DependencyObject element) =>
        (bool)element.GetValue(ShowDropDownButtonProperty);

    public static void SetShowDropDownButton(DependencyObject element, bool value) =>
        element.SetValue(ShowDropDownButtonProperty, value);
}
