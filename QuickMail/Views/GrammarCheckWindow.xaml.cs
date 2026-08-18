using System.Windows;
using QuickMail.Services;

namespace QuickMail.Views;

public sealed class GrammarIssueRow
{
    public GrammarIssue Issue { get; }
    public string Message => Issue.Message;
    public IReadOnlyList<string> Replacements => Issue.Replacements;
    public bool HasReplacements => Replacements.Count > 0;
    public Visibility NoReplacementVisibility => HasReplacements ? Visibility.Collapsed : Visibility.Visible;
    public string? SelectedReplacement { get; set; }

    public GrammarIssueRow(GrammarIssue issue)
    {
        Issue = issue;
        SelectedReplacement = issue.Replacements.Count == 0 ? null : issue.Replacements[0];
    }

    public GrammarIssue WithSelectedReplacement() => Issue with
    {
        Replacements = string.IsNullOrEmpty(SelectedReplacement) ? [] : [SelectedReplacement],
    };
}

public partial class GrammarCheckWindow : Window
{
    private readonly string _original;
    private readonly IReadOnlyList<GrammarIssueRow> _rows;
    public string CorrectedText => CorrectedBox.Text;

    public GrammarCheckWindow(string original, IReadOnlyList<GrammarIssue> issues)
    {
        InitializeComponent();
        _original = original;
        _rows = issues.Select(issue => new GrammarIssueRow(issue)).ToArray();
        SummaryText.Text = issues.Count == 1 ? "1 possible issue found." : $"{issues.Count} possible issues found.";
        DataContext = _rows;
        CorrectedBox.Text = original;
    }

    private GrammarIssue[] SelectedIssues() => IssuesList.SelectedItems.Cast<GrammarIssueRow>()
        .Select(row => row.WithSelectedReplacement()).ToArray();

    private void IssuesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selected = SelectedIssues();
        CorrectedBox.Text = LanguageToolService.ApplyPreferredReplacements(_original, selected);
    }

    private void ApplySelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedIssues();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Select one or more corrections first.", "Grammar Check",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        CorrectedBox.Text = LanguageToolService.ApplyPreferredReplacements(_original, selected);
        DialogResult = true;
    }

    private void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        CorrectedBox.Text = LanguageToolService.ApplyPreferredReplacements(_original,
            _rows.Select(row => row.WithSelectedReplacement()));
        DialogResult = true;
    }
}
