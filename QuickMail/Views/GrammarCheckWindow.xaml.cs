using System.Windows;
using QuickMail.Services;

namespace QuickMail.Views;

public sealed record GrammarIssueRow(string Message, string ReplacementSummary);

public partial class GrammarCheckWindow : Window
{
    public string CorrectedText => CorrectedBox.Text;

    public GrammarCheckWindow(string original, IReadOnlyList<GrammarIssue> issues)
    {
        InitializeComponent();
        SummaryText.Text = issues.Count == 1 ? "1 possible issue found." : $"{issues.Count} possible issues found.";
        DataContext = issues.Select(issue => new GrammarIssueRow(issue.Message,
            issue.Replacements.Count == 0 ? "No automatic replacement" :
            $"Suggestions: {string.Join(", ", issue.Replacements)}")).ToArray();
        CorrectedBox.Text = LanguageToolService.ApplyPreferredReplacements(original, issues);
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
