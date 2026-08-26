using QuickMail.Models;

namespace QuickMail.Helpers;

/// <summary>
/// Converts a Windows <c>mailto:</c> protocol activation into a new-message model.
/// Parsing lives outside WPF so command-line and single-instance activation follow
/// exactly the same path and can be covered by small unit tests.
/// </summary>
internal static class MailtoUriParser
{
    public static ComposeModel? ParseArguments(IEnumerable<string> args)
    {
        var values = args.ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].Equals("--mailto", StringComparison.OrdinalIgnoreCase))
                return i + 1 < values.Length ? Parse(values[i + 1]) : null;

            // Also accept the protocol URI directly. This makes the command-line
            // handler tolerant of manually-created Windows registrations.
            if (values[i].StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                return Parse(values[i]);
        }

        return null;
    }

    public static ComposeModel? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            HasInvalidPercentEncoding(value))
            return null;

        try
        {
            var payload = value["mailto:".Length..];
            var queryAt = payload.IndexOf('?');
            var path = queryAt >= 0 ? payload[..queryAt] : payload;
            var query = queryAt >= 0 ? payload[(queryAt + 1)..] : string.Empty;

            var to = new List<string>();
            AddAddresses(to, Decode(path));
            var cc = new List<string>();
            var bcc = new List<string>();
            var subject = string.Empty;
            var body = string.Empty;

            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equalsAt = pair.IndexOf('=');
                var key = Decode(equalsAt >= 0 ? pair[..equalsAt] : pair);
                var fieldValue = Decode(equalsAt >= 0 ? pair[(equalsAt + 1)..] : string.Empty);

                if (key.Equals("to", StringComparison.OrdinalIgnoreCase))
                    AddAddresses(to, fieldValue);
                else if (key.Equals("cc", StringComparison.OrdinalIgnoreCase))
                    AddAddresses(cc, fieldValue);
                else if (key.Equals("bcc", StringComparison.OrdinalIgnoreCase))
                    AddAddresses(bcc, fieldValue);
                else if (key.Equals("subject", StringComparison.OrdinalIgnoreCase))
                    subject = JoinRepeated(subject, SingleLine(fieldValue));
                else if (key.Equals("body", StringComparison.OrdinalIgnoreCase))
                    body = JoinRepeated(body, fieldValue, Environment.NewLine);
            }

            return new ComposeModel
            {
                Kind = ComposeKind.NewMessage,
                To = string.Join(", ", to),
                Cc = string.Join(", ", cc),
                Bcc = string.Join(", ", bcc),
                Subject = subject,
                Body = body,
            };
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value);

    private static bool HasInvalidPercentEncoding(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') continue;
            if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
                return true;
            i += 2;
        }
        return false;
    }

    private static void AddAddresses(ICollection<string> target, string value)
    {
        foreach (var address in SingleLine(value).Split([',', ';'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            target.Add(address);
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string JoinRepeated(string current, string value, string separator = ", ")
    {
        if (string.IsNullOrEmpty(value)) return current;
        return string.IsNullOrEmpty(current) ? value : current + separator + value;
    }
}
