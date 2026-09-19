namespace QuickMail.Models;

/// <summary>Read-only result of simulating one client rule against a mailbox scope.</summary>
public sealed record RulePreviewResult(
    string RuleName,
    string SourceScope,
    string ActionDescription,
    int ScannedCount,
    int MatchCount,
    int IncomingCount,
    int OutgoingCount,
    int MarkReadCount,
    IReadOnlyList<MailMessageSummary> SampleMessages);
