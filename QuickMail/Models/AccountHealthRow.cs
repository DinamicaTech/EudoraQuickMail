namespace QuickMail.Models;

/// <summary>One read-only account diagnostics snapshot shown by Account Health.</summary>
public sealed record AccountHealthRow(
    string Account,
    string State,
    string Protocol,
    string Incoming,
    string Outgoing,
    string Authentication,
    string LastDownload,
    string Queue,
    string LastError,
    bool HasProblem);
