using System.Windows;
using QuickMail.Models;

namespace QuickMail.Views;

public partial class FolderAnalysisWindow : Window
{
    public FolderAnalysisWindow(string folderName, IReadOnlyList<FolderDomainSummary> rows)
    {
        InitializeComponent();
        ScopeText.Text = $"Top sender domains in {folderName}, ordered by message count.";
        DomainGrid.ItemsSource = rows;
        SummaryText.Text = rows.Count == 0
            ? "No sender domains found."
            : $"Showing the top {rows.Count:N0} sender domain{(rows.Count == 1 ? string.Empty : "s")}.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
