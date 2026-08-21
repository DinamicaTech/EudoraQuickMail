using System.Net;
using System.Text.RegularExpressions;
using MimeKit;

namespace EudoraImporter;

internal static partial class MessageTextExtractor
{
    internal sealed record ExtractedMessage(string PlainText, string Html, IReadOnlyList<string> AttachmentPaths);

    public static ExtractedMessage? ExtractEudoraWrappedHtml(string rawMessage)
    {
        var match = EudoraHtmlBlock().Match(rawMessage);
        if (!match.Success) return null;
        var html = match.Groups["html"].Value.Trim();
        if (!LooksLikeHtml(html)) return null;
        return new ExtractedMessage(HtmlToText(html), html, []);
    }

    public static ExtractedMessage ExtractAll(MimeMessage message, string sourceDirectory, string? mailboxPath = null)
    {
        var plain = message.TextBody ?? ExtractEntity(message.Body);
        var html = message.HtmlBody ?? string.Empty;
        // Old Eudora messages often contain a complete HTML document without declaring a
        // Content-Type. MimeKit correctly exposes that payload as text/plain; preserve it as HTML
        // and derive a readable/searchable plain-text representation.
        if (string.IsNullOrWhiteSpace(html) && LooksLikeHtml(plain))
        {
            html = plain;
            plain = HtmlToText(html);
        }
        var attachmentPaths = ExtractAttachmentPaths(plain + "\n" + HtmlToText(html), sourceDirectory, mailboxPath);

        if (string.IsNullOrWhiteSpace(plain) && !string.IsNullOrWhiteSpace(html))
            plain = HtmlToText(html);
        plain = EudoraAttachmentLine().Replace(plain, string.Empty);
        plain = EudoraEmbeddedLine().Replace(plain, string.Empty);
        plain = EudoraEmbeddedTag().Replace(plain, string.Empty);
        plain = ExcessBlankLines().Replace(plain, "\n\n").Trim();
        return new ExtractedMessage(plain, html, attachmentPaths);
    }

    public static IReadOnlyList<string> ExtractAttachmentPaths(string rawMessage, string sourceDirectory,
        string? mailboxPath = null) =>
        AttachmentPath().Matches(rawMessage)
            .Select(match => ResolveAttachmentPath(match.Groups["path"].Value.Trim().Trim('"'), sourceDirectory))
            .Concat(EmbeddedPath().Matches(rawMessage)
                .Select(match => ResolveEmbeddedPath(match.Groups["name"].Value.Trim(), sourceDirectory, mailboxPath)))
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool LooksLikeHtml(string value) =>
        value.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("<body", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("<div", StringComparison.OrdinalIgnoreCase);

    public static string Extract(MimeMessage message)
    {
        var text = ExtractEntity(message.Body);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        text = EudoraAttachmentLine().Replace(text, string.Empty);
        text = EudoraEmbeddedLine().Replace(text, string.Empty);
        text = EudoraEmbeddedTag().Replace(text, string.Empty);
        return ExcessBlankLines().Replace(text, "\n\n").Trim();
    }

    private static string? ResolveAttachmentPath(string value, string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Path.IsPathFullyQualified(value)) return Path.GetFullPath(value);
        foreach (var candidate in new[]
        {
            Path.Combine(sourceDirectory, value),
            Path.Combine(sourceDirectory, "Attach", value),
            Path.Combine(sourceDirectory, "Attachments", value),
        })
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        // Retain the most likely link even when the disk is temporarily unavailable.
        return Path.GetFullPath(Path.Combine(sourceDirectory, "Attach", value));
    }

    private static string? ResolveEmbeddedPath(string fileName, string sourceDirectory, string? mailboxPath)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        fileName = Path.GetFileName(fileName);

        // Rotated Eudora mailboxes conventionally use matching EmbeddedYYYY folders.
        // Prefer that exact year, then the current Embedded folder. Only fall back to
        // another yearly folder when the filename is unique, avoiding a wrong image
        // when Eudora reused names such as image001.png in several years.
        var year = mailboxPath is null ? null : MailboxYear().Match(Path.GetFileNameWithoutExtension(mailboxPath)).Value;
        var preferred = new List<string>();
        if (!string.IsNullOrEmpty(year)) preferred.Add(Path.Combine(sourceDirectory, "Embedded" + year, fileName));
        preferred.Add(Path.Combine(sourceDirectory, "Embedded", fileName));
        var direct = preferred.FirstOrDefault(File.Exists);
        if (direct is not null) return Path.GetFullPath(direct);

        var matches = Directory.Exists(sourceDirectory)
            ? Directory.EnumerateDirectories(sourceDirectory, "Embedded*")
                .Select(directory => Path.Combine(directory, fileName))
                .Where(File.Exists).Take(2).ToList()
            : [];
        return matches.Count == 1 ? Path.GetFullPath(matches[0]) : null;
    }

    private static string ExtractEntity(MimeEntity? entity)
    {
        if (entity is null || IsExternalContent(entity)) return string.Empty;
        if (entity is TextPart text)
            return text.IsHtml ? HtmlToText(text.Text) : text.Text;
        if (entity is MessagePart) return string.Empty;
        if (entity is MultipartAlternative alternative)
        {
            var plain = alternative.OfType<TextPart>().FirstOrDefault(p => !p.IsHtml && !IsExternalContent(p));
            if (plain is not null) return plain.Text;
            var html = alternative.OfType<TextPart>().FirstOrDefault(p => p.IsHtml && !IsExternalContent(p));
            return html is null ? string.Empty : HtmlToText(html.Text);
        }
        if (entity is MultipartRelated related)
            return related.Count == 0 ? string.Empty : ExtractEntity(related[0]);
        if (entity is Multipart multipart)
            return string.Join("\n", multipart.Select(ExtractEntity).Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.Empty;
    }

    private static bool IsExternalContent(MimeEntity entity) =>
        entity.IsAttachment ||
        (entity.ContentDisposition?.Disposition.Equals("inline", StringComparison.OrdinalIgnoreCase) == true &&
         !string.IsNullOrEmpty(entity.ContentId)) ||
        entity.ContentType.MimeType.Equals("message/external-body", StringComparison.OrdinalIgnoreCase);

    private static string HtmlToText(string html)
    {
        var withoutNoise = ScriptOrStyle().Replace(html, string.Empty);
        var withLines = BlockBreak().Replace(withoutNoise, "\n");
        return WebUtility.HtmlDecode(HtmlTag().Replace(withLines, string.Empty));
    }

    [GeneratedRegex(@"(?im)^\s*Attachment Converted:\s*.*(?:\r?\n|$)")]
    private static partial Regex EudoraAttachmentLine();
    [GeneratedRegex(@"(?im)^\s*Embedded Content:\s*.*(?:\r?\n|$)")]
    private static partial Regex EudoraEmbeddedLine();
    [GeneratedRegex("(?im)^\\s*Attachment Converted:\\s*(?:\\\"(?<path>[^\\\"]+)\\\"|(?<path>.+?))\\s*$")]
    private static partial Regex AttachmentPath();
    [GeneratedRegex(@"(?im)^\s*Embedded Content:\s*(?<name>.+?):\s*[0-9a-f]{8}(?:,[0-9a-f]{8}){3}\s*$")]
    private static partial Regex EmbeddedPath();
    [GeneratedRegex(@"(?:19|20)\d{2}")]
    private static partial Regex MailboxYear();
    [GeneratedRegex(@"(?is)</?x-(?:embedded|eudora-option)[^>]*>")]
    private static partial Regex EudoraEmbeddedTag();
    [GeneratedRegex(@"(?is)<x-html\b[^>]*>\s*(?<html>.*?)\s*</x-html\s*>")]
    private static partial Regex EudoraHtmlBlock();
    [GeneratedRegex(@"(?is)<(script|style)\b[^>]*>.*?</\1\s*>")]
    private static partial Regex ScriptOrStyle();
    [GeneratedRegex(@"(?i)<(?:br\s*/?|/p|/div|/li|/tr|/h[1-6])\s*>")]
    private static partial Regex BlockBreak();
    [GeneratedRegex(@"(?s)<[^>]+>")]
    private static partial Regex HtmlTag();
    [GeneratedRegex(@"(?:\r?\n[ \t]*){3,}")]
    private static partial Regex ExcessBlankLines();
}
