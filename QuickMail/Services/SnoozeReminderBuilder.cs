using System.Globalization;
using System.Net;
using QuickMail.Models;

namespace QuickMail.Services;

public sealed record SnoozeReminderTarget(
    Guid AccountId,
    string FolderName,
    string MessageId,
    string InternetMessageId);

/// <summary>Builds the small self-addressed message used to surface a long snooze at the top of In.</summary>
public static class SnoozeReminderBuilder
{
    private const string Prefix = "quickmail:message?";

    public static ComposeModel Build(MailMessageSummary original, AccountModel sender)
    {
        var subject = string.IsNullOrWhiteSpace(original.Subject) ? "(no subject)" : original.Subject.Trim();
        var link = BuildLink(original);
        var preview = Compact(original.Preview, 600);
        var originalDate = original.Date.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
        var plain = $"Reminder for a snoozed message\r\n\r\n" +
                    $"Subject: {subject}\r\nFrom: {original.From}\r\nOriginal date: {originalDate}\r\n" +
                    (preview.Length == 0 ? string.Empty : $"\r\n{preview}\r\n") +
                    $"\r\nOpen the original message in Eudora QuickMail:\r\n{link}";
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"></head><body>" +
                   "<h2>Reminder for a snoozed message</h2>" +
                   $"<p><strong>Subject:</strong> {WebUtility.HtmlEncode(subject)}<br>" +
                   $"<strong>From:</strong> {WebUtility.HtmlEncode(original.From)}<br>" +
                   $"<strong>Original date:</strong> {WebUtility.HtmlEncode(originalDate)}</p>" +
                   (preview.Length == 0 ? string.Empty : $"<blockquote>{WebUtility.HtmlEncode(preview)}</blockquote>") +
                   $"<p><a href=\"{WebUtility.HtmlEncode(link)}\"><strong>Open original message in Eudora QuickMail</strong></a></p>" +
                   "</body></html>";

        return new ComposeModel
        {
            Kind = ComposeKind.NewMessage,
            AccountId = sender.Id,
            To = sender.Username,
            Subject = $"Reminder: {subject}",
            Body = plain,
            HtmlBody = html,
            Mode = ComposeMode.Html,
        };
    }

    public static string BuildLink(MailMessageSummary message) => Prefix +
        $"account={Uri.EscapeDataString(message.AccountId.ToString("D"))}" +
        $"&folder={Uri.EscapeDataString(message.FolderName)}" +
        $"&id={Uri.EscapeDataString(message.MessageId)}" +
        $"&internet={Uri.EscapeDataString(message.InternetMessageId ?? string.Empty)}";

    public static bool TryParseLink(string? uri, out SnoozeReminderTarget target)
    {
        target = new SnoozeReminderTarget(Guid.Empty, string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var values = uri[Prefix.Length..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2)
            .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]),
                StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("account", out var accountText) || !Guid.TryParse(accountText, out var accountId)
            || !values.TryGetValue("folder", out var folder) || string.IsNullOrWhiteSpace(folder)
            || !values.TryGetValue("id", out var messageId) || string.IsNullOrWhiteSpace(messageId))
            return false;

        values.TryGetValue("internet", out var internetMessageId);
        target = new SnoozeReminderTarget(accountId, folder, messageId, internetMessageId ?? string.Empty);
        return true;
    }

    private static string Compact(string? text, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var compact = string.Join(' ', text.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= maximumLength ? compact : compact[..maximumLength].TrimEnd() + "…";
    }
}
