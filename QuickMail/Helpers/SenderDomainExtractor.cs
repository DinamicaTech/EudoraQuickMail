namespace QuickMail.Helpers;

/// <summary>Extracts a normalized domain from a display-name or mailbox-formatted From value.</summary>
public static class SenderDomainExtractor
{
    public const string MissingDomain = "(No sender domain)";

    public static string Extract(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return MissingDomain;
        var at = from.LastIndexOf('@');
        if (at < 0 || at == from.Length - 1) return MissingDomain;

        var start = at + 1;
        var end = start;
        while (end < from.Length && !IsTerminator(from[end])) end++;
        var domain = from[start..end].Trim().TrimEnd('.');
        return domain.Length == 0 ? MissingDomain : domain.ToLowerInvariant();
    }

    private static bool IsTerminator(char value) =>
        char.IsWhiteSpace(value) || value is '>' or '<' or ',' or ';' or '"' or '\'' or ')' or '(';
}
