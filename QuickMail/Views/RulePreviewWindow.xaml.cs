using System.Windows;
using QuickMail.Models;

namespace QuickMail.Views;

public partial class RulePreviewWindow : Window
{
    public RulePreviewWindow(RulePreviewResult result)
    {
        InitializeComponent();
        DataContext = new
        {
            result.RuleName,
            result.ActionDescription,
            result.SampleMessages,
            Summary = $"Scope: {result.SourceScope}  ·  Scanned: {result.ScannedCount:N0}  ·  " +
                      $"Matches: {result.MatchCount:N0}  ·  Incoming: {result.IncomingCount:N0}  ·  " +
                      $"Outgoing: {result.OutgoingCount:N0}  ·  Would mark read: {result.MarkReadCount:N0}"
        };
    }
}
