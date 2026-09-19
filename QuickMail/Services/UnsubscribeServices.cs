using System.Net;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuickMail.Models;

namespace QuickMail.Services;

internal static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, IndentedJson));
        File.Move(temp, path, overwrite: true);
    }
}

public sealed record UnsubscribeCandidate(Uri Url, string SourceKey);

/// <summary>Finds an HTTP unsubscribe endpoint without executing sender-controlled content.</summary>
public static partial class UnsubscribeDetector
{
    private static readonly string[] LinkWords =
    [
        "unsubscribe", "unsubscrib", "opt out", "opt-out", "remove me",
        "darse de baja", "darme de baja", "desuscrib", "cancelar suscrip",
        "baixa", "cancel·lar subscrip", "cancelar subscrip"
    ];

    public static UnsubscribeCandidate? Detect(MailMessageDetail? message)
    {
        if (message is null) return null;
        var source = SenderAddress(message.From);
        if (string.IsNullOrWhiteSpace(source)) return null;

        var header = HeaderValue(message.RawHeaders, "List-Unsubscribe");
        foreach (Match match in HttpUrlRegex().Matches(header))
            if (TryUri(WebUtility.HtmlDecode(match.Value.Trim('<', '>', ' ', '\t')), out var uri))
                return new(uri, source);

        var best = HtmlAnchorRegex().Matches(message.HtmlBody ?? string.Empty)
            .Cast<Match>()
            .Select(match => new
            {
                Url = WebUtility.HtmlDecode(match.Groups[1].Value),
                Text = WebUtility.HtmlDecode(StripTags(match.Groups[2].Value))
            })
            .Select(item => (item, score: Score(item.Url, item.Text)))
            .Where(item => item.score > 0 && TryUri(item.item.Url, out _))
            .OrderByDescending(item => item.score)
            .FirstOrDefault();
        if (best.item is not null && TryUri(best.item.Url, out var htmlUri))
            return new(htmlUri, source);

        foreach (Match match in HttpUrlRegex().Matches(message.PlainTextBody ?? string.Empty))
        {
            var raw = match.Value.TrimEnd('.', ',', ';', ')', ']', '>', '\'', '"');
            if (Score(raw, raw) > 0 && TryUri(raw, out var uri)) return new(uri, source);
        }
        return null;
    }

    private static int Score(string url, string text)
    {
        var haystack = (url + " " + text).ToLowerInvariant();
        var score = 0;
        foreach (var word in LinkWords)
            if (haystack.Contains(word, StringComparison.Ordinal)) score += 10;
        if (haystack.Contains("preference", StringComparison.Ordinal)) score += 2;
        return score;
    }

    private static bool TryUri(string value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string SenderAddress(string value)
    {
        var match = EmailRegex().Match(value ?? string.Empty);
        return match.Success ? match.Value.Trim().ToLowerInvariant() : string.Empty;
    }

    private static string HeaderValue(string raw, string name)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var unfolded = Regex.Replace(raw, "\\r?\\n[ \\t]+", " ");
        var match = Regex.Match(unfolded, $"(?im)^{Regex.Escape(name)}\\s*:\\s*(.+)$");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string StripTags(string value) => Regex.Replace(value, "<[^>]+>", " ");

    [GeneratedRegex("https?://[^\\s<>\\\"]+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrlRegex();
    [GeneratedRegex("<a\\b[^>]*href\\s*=\\s*[\\\"']([^\\\"']+)[\\\"'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlAnchorRegex();
    [GeneratedRegex("[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();
}

public sealed class UnsubscribePreferenceService
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly HashSet<string> _maintained;

    public UnsubscribePreferenceService(ProfileContext profile)
    {
        _path = Path.Combine(profile.ProfileDir, "unsubscribe-preferences.json");
        _maintained = Load();
    }

    public bool IsMaintained(string sourceKey)
    {
        lock (_gate) return _maintained.Contains(Normalize(sourceKey));
    }

    public void Maintain(string sourceKey)
    {
        lock (_gate)
        {
            if (!_maintained.Add(Normalize(sourceKey))) return;
            AtomicJsonFile.Write(_path, _maintained.OrderBy(value => value).ToArray());
        }
    }

    private HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            var values = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path)) ?? [];
            return new(values.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LogService.Log("Load unsubscribe preferences", ex);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
