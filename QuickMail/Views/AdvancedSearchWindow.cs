using System.Windows;
using System.Windows.Controls;
using QuickMail.Models;

namespace QuickMail.Views;

public sealed class AdvancedSearchWindow : Window
{
    private static readonly string[] JoinValues = ["AND", "OR"];
    private static readonly string[] FieldValues = ["Date", "From", "To", "CC", "Subject", "Body"];
    private static readonly string[] OperatorValues = ["contains", "<", "<=", "=", ">=", ">"];
    private readonly StackPanel _rows = new();
    private static List<AdvancedSearchCriterion> _sessionCriteria = [];
    public event Func<IReadOnlyList<AdvancedSearchCriterion>, Task>? SearchRequested;

    public AdvancedSearchWindow()
    {
        Title = "Advanced Search";
        Width = 700; Height = 360; MinWidth = 560; MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("Theme.WindowBackground");
        Foreground = (System.Windows.Media.Brush)FindResource("Theme.TextPrimary");
        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var add = new Button { Content = "Add criterion", MinWidth = 110, Margin = new Thickness(0, 8, 8, 0) };
        var search = new Button { Content = "Search", MinWidth = 90, Margin = new Thickness(0, 8, 8, 0), IsDefault = true };
        var close = new Button { Content = "Close", MinWidth = 90, Margin = new Thickness(0, 8, 0, 0), IsCancel = true };
        add.Click += (_, _) => AddRow();
        search.Click += async (_, _) =>
        {
            var criteria = _rows.Children.OfType<Grid>().Select(ReadRow).Where(c => !string.IsNullOrWhiteSpace(c.Value)).ToList();
            if (criteria.Count > 0 && SearchRequested is not null)
            {
                _sessionCriteria = criteria;
                await SearchRequested(criteria);
                Close();
            }
        };
        close.Click += (_, _) => Close();
        buttons.Children.Add(add); buttons.Children.Add(search); buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        if (_sessionCriteria.Count == 0) AddRow();
        else foreach (var criterion in _sessionCriteria) AddRow(criterion);
    }

    private void AddRow(AdvancedSearchCriterion? criterion = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35) });
        var join = Combo(JoinValues, 0);
        var field = Combo(FieldValues, 1);
        var op = Combo(OperatorValues, 2);
        var value = new TextBox { Margin = new Thickness(4, 0, 4, 0) }; Grid.SetColumn(value, 3);
        var remove = new Button { Content = "×", ToolTip = "Remove criterion" }; Grid.SetColumn(remove, 4);
        remove.Click += (_, _) => { if (_rows.Children.Count > 1) _rows.Children.Remove(row); };
        row.Children.Add(join); row.Children.Add(field); row.Children.Add(op); row.Children.Add(value); row.Children.Add(remove);
        if (criterion is not null)
        {
            join.SelectedItem = criterion.Join;
            field.SelectedItem = criterion.Field;
            op.SelectedItem = criterion.Operator;
            value.Text = criterion.Value;
        }
        if (_rows.Children.Count == 0) { join.IsEnabled = false; join.SelectedItem = "AND"; }
        _rows.Children.Add(row);
        value.Focus();
    }

    private static ComboBox Combo(IEnumerable<string> values, int column)
    {
        var box = new ComboBox { ItemsSource = values, SelectedIndex = 0, Margin = new Thickness(4, 0, 4, 0) };
        Grid.SetColumn(box, column); return box;
    }

    private static AdvancedSearchCriterion ReadRow(Grid row)
    {
        var controls = row.Children;
        return new((string)((ComboBox)controls[1]).SelectedItem, (string)((ComboBox)controls[2]).SelectedItem,
            ((TextBox)controls[3]).Text, (string)((ComboBox)controls[0]).SelectedItem);
    }
}
