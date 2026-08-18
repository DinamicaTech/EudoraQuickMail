using System.Globalization;
using System.Text;

namespace QuickMail.Models;

public enum QuickSearchField { Any, To, From, Cc, Subject, Body, AttachmentCount, AttachmentName, AttachmentContent, Date }

public sealed record QuickSearchTerm(QuickSearchField Field, string Operator, string Value);
public sealed record QuickSearchGroup(IReadOnlyList<QuickSearchTerm> Alternatives);
public sealed record ParsedQuickSearch(IReadOnlyList<QuickSearchGroup> Groups);

/// <summary>Parser for the compact search language used by the main search box.</summary>
public static class QuickSearchParser
{
    public static ParsedQuickSearch Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new([]);
        var groups = new List<QuickSearchGroup>();
        foreach (var raw in SplitTopLevel(input, ';'))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;
            if (IsEntirelyQuoted(token))
            {
                groups.Add(new([new(QuickSearchField.Any, ":", Unquote(token))]));
                continue;
            }

            var parsed = ParseLeading(token);
            if (parsed.Value.Length >= 2 && parsed.Value[0] == '(' && parsed.Value[^1] == ')')
            {
                var alternatives = SplitTopLevel(parsed.Value[1..^1], ';')
                    .Select(v => new QuickSearchTerm(parsed.Field, parsed.Operator, Unquote(v.Trim())))
                    .Where(v => v.Value.Length > 0).ToList();
                if (alternatives.Count == 0) throw new FormatException("An OR group cannot be empty.");
                groups.Add(new(alternatives));
            }
            else groups.Add(new([parsed with { Value = Unquote(parsed.Value.Trim()) }]));
        }
        return new(groups);
    }

    private static QuickSearchTerm ParseLeading(string token)
    {
        var prefixes = new (string Prefix, QuickSearchField Field)[]
        {
            ("A#", QuickSearchField.AttachmentCount), ("AN", QuickSearchField.AttachmentName),
            ("AC", QuickSearchField.AttachmentContent), ("T", QuickSearchField.To),
            ("F", QuickSearchField.From), ("C", QuickSearchField.Cc),
            ("S", QuickSearchField.Subject), ("B", QuickSearchField.Body), ("D", QuickSearchField.Date),
        };
        foreach (var (prefix, field) in prefixes)
        {
            if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = token[prefix.Length..];
            var op = rest.StartsWith(">=") || rest.StartsWith("<=") ? rest[..2]
                : rest.Length > 0 && rest[0] is ':' or '=' or '<' or '>' ? rest[..1] : string.Empty;
            if (op.Length == 0) continue; // ordinary text beginning with a prefix letter
            if (field is QuickSearchField.AttachmentCount or QuickSearchField.Date)
            {
                if (op == ":") throw new FormatException($"{prefix} requires =, <, <=, > or >=.");
            }
            else if (op != ":") throw new FormatException($"{prefix} is a text field and requires ':'.");
            var value = rest[op.Length..].Trim();
            if (value.Length == 0) throw new FormatException($"Missing value after {prefix}{op}.");
            return new(field, op, value);
        }
        return new(QuickSearchField.Any, ":", token);
    }

    public static (DateTimeOffset Start, DateTimeOffset End) ParseDateRange(string value)
    {
        var culture = CultureInfo.CurrentCulture;
        if (int.TryParse(value, out var year) && year is >= 1 and <= 9999)
            return Range(new DateTime(year, 1, 1), new DateTime(year, 1, 1).AddYears(1));
        var monthYear = value.Split('/', StringSplitOptions.TrimEntries);
        if (monthYear.Length == 2 && int.TryParse(monthYear[0], out var month)
            && int.TryParse(monthYear[1], out year) && month is >= 1 and <= 12 && year is >= 1 and <= 9999)
            return Range(new DateTime(year, month, 1), new DateTime(year, month, 1).AddMonths(1));
        if (DateTime.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out var date))
            return Range(date.Date, date.Date.AddDays(1));
        throw new FormatException($"Invalid date '{value}'. Use a local date, MM/yyyy, or yyyy.");

        static (DateTimeOffset, DateTimeOffset) Range(DateTime start, DateTime end) =>
            (new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)).ToUniversalTime(),
             new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end)).ToUniversalTime());
    }

    private static IReadOnlyList<string> SplitTopLevel(string text, char separator)
    {
        var result = new List<string>(); var current = new StringBuilder(); var depth = 0; var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '(') depth++;
            else if (!quoted && c == ')') { if (--depth < 0) throw new FormatException("Unbalanced parentheses."); }
            if (c == separator && !quoted && depth == 0) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        if (quoted) throw new FormatException("Unclosed quotation mark.");
        if (depth != 0) throw new FormatException("Unbalanced parentheses.");
        result.Add(current.ToString()); return result;
    }

    private static bool IsEntirelyQuoted(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"';
    private static string Unquote(string value) => IsEntirelyQuoted(value) ? value[1..^1].Replace("\"\"", "\"") : value;
}
