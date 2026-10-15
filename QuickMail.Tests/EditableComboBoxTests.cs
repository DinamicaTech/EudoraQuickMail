using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using QuickMail.Controls;

namespace QuickMail.Tests;

public class EditableComboBoxTests
{
    private static ComboBox Create(bool editable, bool showArrow = true)
    {
        WpfTestApplication.EnsureStarted();
        var resources = new ResourceDictionary
        {
            Source = WpfTestApplication.ResourceUri("Styles/ThemedControls.xaml"),
        };
        var combo = new ComboBox { Width = 100, Height = 30, IsEditable = editable,
            Style = (Style)resources[typeof(ComboBox)], ItemsSource = new[] { 0, 2, 7, 14, 30 } };
        // Always render the decoration: this must not depend on whether another test
        // populated application theme resources before us.
        combo.Resources["Theme.TextSecondary"] = Brushes.Black;
        ComboBoxAssist.SetShowDropDownButton(combo, showArrow);
        combo.ApplyTemplate();
        combo.Measure(new Size(100, 30));
        combo.Arrange(new Rect(0, 0, 100, 30));
        combo.UpdateLayout();
        return combo;
    }

    [WpfFact]
    public void EditableArrowHitTargetsToggleNotEditor()
    {
        var combo = Create(true);
        // Respect input transparency without a native presentation source. The simple
        // VisualTreeHelper.HitTest overload also returns decorative IsHitTestVisible=false
        // visuals (the arrow); InputHitTest requires a visible/presented control.
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(combo,
            node => node is UIElement element && (!element.IsHitTestVisible || element.Visibility != Visibility.Visible)
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; },
            new PointHitTestParameters(new Point(87, 15)));
        while (hit != null && hit is not ToggleButton) hit = VisualTreeHelper.GetParent(hit);
        Assert.Same(combo.Template.FindName("toggle", combo), hit);
        var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
        Assert.True(editor.ActualWidth < 80);
        editor.Text = "12";
        Assert.Equal("12", combo.Text);
        var binding = System.Windows.Data.BindingOperations.GetBinding((ToggleButton)hit!, ToggleButton.IsCheckedProperty);
        Assert.Equal("IsDropDownOpen", binding!.Path.Path);
        Assert.Equal(System.Windows.Data.BindingMode.TwoWay, binding.Mode);
    }

    [WpfFact]
    public void HiddenArrowKeepsFullWidthEditorForSearchHistory()
    {
        var combo = Create(true, false);
        var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
        Assert.Equal(new Thickness(1), editor.Margin);
        Assert.True(editor.ActualWidth > 90);
        Assert.Equal(Visibility.Collapsed, ((ToggleButton)combo.Template.FindName("toggle", combo)).Visibility);
    }

    [WpfFact]
    public void NonEditableComboRetainsFullWidthToggle()
    {
        var combo = Create(false);
        var toggle = Assert.IsType<ToggleButton>(combo.Template.FindName("toggle", combo));
        Assert.Equal(100, toggle.ActualWidth);
        Assert.Equal(Visibility.Hidden, ((TextBox)combo.Template.FindName("PART_EditableTextBox", combo)).Visibility);
    }
}
