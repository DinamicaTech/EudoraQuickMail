using System;
using System.IO;
using System.Linq;
using System.Text;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Shared recognition and decoding for iCalendar MIME parts. Calendar files are frequently sent
/// as application/octet-stream (or application/ics) with only the .ics extension identifying
/// them, so relying exclusively on text/calendar loses otherwise valid invitations.
/// </summary>
internal static class CalendarMimeHelper
{
    internal const int MaxCalendarBytes = 2 * 1024 * 1024;

    internal static bool IsCalendarPart(MimePart part) =>
        part.ContentType.IsMimeType("text", "calendar") ||
        string.Equals(Path.GetExtension(part.FileName), ".ics", StringComparison.OrdinalIgnoreCase);

    internal static string? FindCalendarText(MimeMessage message)
    {
        string? firstReadable = null;
        foreach (var part in message.BodyParts.OfType<MimePart>().Where(IsCalendarPart))
        {
            var raw = ReadCalendarText(part);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            firstReadable ??= raw;
            try
            {
                if (IcsModel.Parse(raw) != null) return raw;
            }
            catch
            {
                // Keep looking: malformed calendar attachments sometimes precede the real invite.
            }
        }
        return firstReadable;
    }

    internal static string? ReadCalendarText(MimePart part)
    {
        if (part.Content is null) return null;
        if (part is TextPart textPart)
        {
            var text = textPart.Text;
            return Encoding.UTF8.GetByteCount(text ?? string.Empty) <= MaxCalendarBytes ? text : null;
        }

        using var stream = new MemoryStream();
        part.Content.DecodeTo(stream);
        if (stream.Length == 0 || stream.Length > MaxCalendarBytes) return null;
        stream.Position = 0;

        Encoding encoding = Encoding.UTF8;
        var charset = part.ContentType.Charset;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch { /* RFC 5545 defaults to UTF-8; retain that safe fallback. */ }
        }
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static void Populate(MailMessageDetail detail, string? rawIcs, string source)
    {
        if (string.IsNullOrWhiteSpace(rawIcs)) return;
        detail.CalendarIcs = rawIcs;
        try
        {
            detail.CalendarInvite = IcsModel.Parse(rawIcs);
        }
        catch (Exception ex)
        {
            // Preserve the raw file in cache even if parsing fails. A parser improvement can then
            // revive already-downloaded messages without another server round-trip.
            LogService.Log($"{source}: failed to parse calendar part for {detail.MessageId}: {ex.Message}");
        }
    }
}
