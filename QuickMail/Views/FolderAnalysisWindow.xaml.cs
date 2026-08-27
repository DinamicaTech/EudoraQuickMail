using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickMail.Models;

namespace QuickMail.Views;

public partial class FolderAnalysisWindow : Window
{
    public string? SelectedDomain { get; private set; }

    public FolderAnalysisWindow(string folderName, IReadOnlyList<FolderDomainSummary> rows)
    {
        InitializeComponent();
        ScopeText.Text = $"Top sender domains in {folderName}, ordered by message count. " +
                         "Double-click a row to show those messages.";
        DomainGrid.ItemsSource = rows;
        SummaryText.Text = rows.Count == 0
            ? "No sender domains found."
            : $"Showing the top {rows.Count:N0} sender domain{(rows.Count == 1 ? string.Empty : "s")}.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DomainGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(DomainGrid, e.OriginalSource as DependencyObject)
                is not DataGridRow ||
            DomainGrid.SelectedItem is not FolderDomainSummary row)
            return;

        SelectedDomain = row.Domain;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
