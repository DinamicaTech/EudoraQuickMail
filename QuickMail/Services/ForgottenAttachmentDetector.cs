using System.Net;
using System.Text.RegularExpressions;

namespace QuickMail.Services;

public static partial class ForgottenAttachmentDetector
{
    public static bool MentionsAttachment(string? plainText, string? htmlText = null)
    {
        var text = !string.IsNullOrWhiteSpace(plainText)
            ? plainText
            : WebUtility.HtmlDecode(HtmlTags().Replace(htmlText ?? string.Empty, " "));
        text = CutQuotedHistory(text);
        return AttachmentPhrase().IsMatch(text);
    }

    private static string CutQuotedHistory(string value)
    {
        var markers = new[] { "-----Original Message-----", "-----Mensaje original-----", "\nOn ", "\nEl " };
        var cut = value.Length;
        foreach (var marker in markers)
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) cut = Math.Min(cut, index);
        }
        return value[..cut];
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTags();

    [GeneratedRegex("\\b(adjunto|adjunta|adjuntamos|anexo|annex|attach(?:ed|ment|ing)?|enclosed|fitxer adjunt|arxiu adjunt)\\b", RegexOptions.IgnoreCase)]
    private static partial Regex AttachmentPhrase();
}
