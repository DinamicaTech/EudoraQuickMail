using System.Net;
using System.Text;
using System.IO;
using MimeKit;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Creates portable EML and HTML representations of a locally cached message.</summary>
public static class MessageExportService
{
    public static string Headers(MailMessageDetail detail)
    {
        if (!string.IsNullOrWhiteSpace(detail.RawHeaders)) return detail.RawHeaders.TrimEnd();
        return $"From: {detail.From}\r\nTo: {detail.To}\r\nCc: {detail.Cc}\r\nBcc: {detail.Bcc}\r\n" +
               $"Reply-To: {detail.ReplyTo}\r\nDate: {detail.Date:R}\r\nSubject: {detail.Subject}\r\n" +
               $"Message-ID: {detail.InternetMessageId}";
    }

    public static string Html(MailMessageDetail detail)
    {
        var heading = $"<section style=\"font:13px 'Segoe UI',Arial,sans-serif;border-bottom:1px solid #bbb;" +
                      $"padding:12px;margin-bottom:12px\"><div><b>Subject:</b> {Encode(detail.Subject)}</div>" +
                      $"<div><b>From:</b> {Encode(detail.From)}</div><div><b>To:</b> {Encode(detail.To)}</div>" +
                      (string.IsNullOrWhiteSpace(detail.Cc) ? "" : $"<div><b>Cc:</b> {Encode(detail.Cc)}</div>") +
                      $"<div><b>Date:</b> {Encode(detail.Date.ToLocalTime().ToString("F"))}</div></section>";
        var body = !string.IsNullOrWhiteSpace(detail.HtmlBody)
            ? detail.HtmlBody
            : $"<pre style=\"white-space:pre-wrap\">{Encode(detail.PlainTextBody)}</pre>";

        var bodyTag = body.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyTag >= 0)
        {
            var close = body.IndexOf('>', bodyTag);
            if (close >= 0) return body.Insert(close + 1, heading);
        }
        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>" +
               Encode(detail.Subject) + "</title></head><body>" + heading + body + "</body></html>";
    }

    public static async Task WriteEmlAsync(MailMessageDetail detail, string path, IMailService mail,
        CancellationToken ct = default)
    {
        var message = LoadHeaderShell(detail.RawHeaders) ?? new MimeMessage();
        EnsureAddresses(message.From, detail.From);
        EnsureAddresses(message.To, detail.To);
        EnsureAddresses(message.Cc, detail.Cc);
        EnsureAddresses(message.Bcc, detail.Bcc);
        if (message.ReplyTo.Count == 0) EnsureAddresses(message.ReplyTo, detail.ReplyTo);
        if (string.IsNullOrWhiteSpace(message.Subject)) message.Subject = detail.Subject;
        if (message.Date == DateTimeOffset.MinValue && detail.Date.Year > 1) message.Date = detail.Date;
        if (string.IsNullOrWhiteSpace(message.MessageId) && !string.IsNullOrWhiteSpace(detail.InternetMessageId))
            message.MessageId = detail.InternetMessageId.Trim('<', '>');

        MimeEntity body = !string.IsNullOrWhiteSpace(detail.HtmlBody)
            ? new MultipartAlternative
            {
                new TextPart("plain") { Text = string.IsNullOrWhiteSpace(detail.PlainTextBody)
                    ? HtmlStripper.ToPlainText(detail.HtmlBody) : detail.PlainTextBody },
                new TextPart("html") { Text = detail.HtmlBody }
            }
            : new TextPart("plain") { Text = detail.PlainTextBody };

        var parts = new List<MimeEntity> { body };
        foreach (var attachment in detail.Attachments)
        {
            var bytes = attachment.Content;
            if (bytes == null && !string.IsNullOrWhiteSpace(attachment.PartSpecifier)
                              && Path.IsPathFullyQualified(attachment.PartSpecifier)
                              && File.Exists(attachment.PartSpecifier))
                bytes = await File.ReadAllBytesAsync(attachment.PartSpecifier, ct);
            if (bytes == null && !string.IsNullOrWhiteSpace(attachment.PartSpecifier))
                bytes = await mail.DownloadAttachmentAsync(detail.AccountId, detail.FolderName,
                    detail.MessageId, attachment.PartSpecifier, ct);
            if (bytes == null) continue;

            var contentType = MimeKit.ContentType.TryParse(attachment.ContentType, out var parsed)
                ? parsed : new MimeKit.ContentType("application", "octet-stream");
            var part = new MimePart(contentType)
            {
                Content = new MimeContent(new MemoryStream(bytes, writable: false)),
                ContentDisposition = new ContentDisposition(attachment.IsInline
                    ? ContentDisposition.Inline : ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = AttachmentSafety.SanitizeFileName(attachment.FileName),
                ContentId = attachment.ContentId
            };
            parts.Add(part);
        }

        if (parts.Count == 1)
        {
            message.Body = body;
        }
        else
        {
            var mixed = new Multipart("mixed");
            foreach (var part in parts) mixed.Add(part);
            message.Body = mixed;
        }
        await message.WriteToAsync(path, ct);
    }

    private static MimeMessage? LoadHeaderShell(string rawHeaders)
    {
        if (string.IsNullOrWhiteSpace(rawHeaders)) return null;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rawHeaders.TrimEnd() + "\r\n\r\n"));
            return MimeMessage.Load(stream);
        }
        catch { return null; }
    }

    private static void EnsureAddresses(InternetAddressList list, string value)
    {
        if (list.Count == 0) AddressParser.AddAddresses(list, value);
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
