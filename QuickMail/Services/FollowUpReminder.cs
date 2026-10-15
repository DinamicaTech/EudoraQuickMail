using System.IO;
using System.Text;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

public static class FollowUpReminder
{
    public const string TrackingHeader = "X-QuickMail-FollowUp-Tracking";

    public static string EncodeTracking(FollowUpTracking tracking) =>
        Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(tracking));

    public static string DraftHeaders(ComposeModel model) =>
        $"X-QuickMail-FollowUp-Days: {model.FollowUpDays}\r\n" +
        (model.FollowUp is { } tracking ? $"{TrackingHeader}: {EncodeTracking(tracking)}\r\n" : "") +
        (model.Repetition is { } plan ? $"{RepeatedSending.Header}: {RepeatedSending.Encode(plan)}\r\n" : "") +
        (model.RuleForwardKey != null ? $"{RuleForwardService.MarkerHeader}: true\r\n" : "");

    public static FollowUpTracking? ReadDraftTracking(string rawHeaders)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rawHeaders + "\r\n\r\n"));
            using var mime = MimeMessage.Load(stream);
            var encoded = mime.Headers[TrackingHeader];
            if (string.IsNullOrWhiteSpace(encoded)) return null;
            var result = System.Text.Json.JsonSerializer.Deserialize<FollowUpTracking>(Convert.FromBase64String(encoded));
            return result != null && !string.IsNullOrWhiteSpace(result.OriginalInternetId)
                && !string.IsNullOrWhiteSpace(result.Recipients) ? result : null;
        }
        catch (FormatException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static HashSet<string> Addresses(string text) => InternetAddressList.TryParse(text, out var list)
        ? list.Mailboxes.Select(m => m.Address).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];

    public static bool IsResponse(FollowUpTracking tracking, MailMessageDetail candidate, string ownAddress)
    {
        if (candidate.Date < tracking.SentAtUtc || candidate.Direction == MessageDirection.Outgoing) return false;
        var senders = Addresses(candidate.From);
        senders.Remove(ownAddress);
        if (!senders.Overlaps(Addresses(tracking.Recipients))) return false;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(candidate.RawHeaders + "\r\n\r\n"));
            using var mime = MimeMessage.Load(stream);
            var auto = mime.Headers["Auto-Submitted"];
            if (!string.IsNullOrWhiteSpace(auto) && !auto.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
            if (mime.Headers.Contains("X-Autoreply") || mime.Headers.Contains("X-Autorespond")) return false;
            if (mime.Headers["Content-Type"]?.Contains("report", StringComparison.OrdinalIgnoreCase) == true) return false;
            var originalId = tracking.OriginalInternetId.Trim().Trim('<', '>');
            return mime.InReplyTo == originalId || mime.References.Contains(originalId);
        }
        catch (FormatException) { return false; }
    }

    public static ComposeModel Build(ComposeModel original, AccountModel account, DateTimeOffset sentAt)
    {
        var summary = new MailMessageSummary
        {
            AccountId = account.Id, FolderName = "Out", MessageId = original.OutgoingMessageId,
            InternetMessageId = original.OutgoingMessageId, From = account.Username,
            Subject = original.Subject, Preview = original.Body, Date = sentAt,
        };
        var reminder = SnoozeReminderBuilder.Build(summary, account);
        reminder.Subject = "Follow up: " + original.Subject;
        var explanation = "No reply detected from: " + original.To;
        reminder.Body = reminder.Body.Replace("Reminder for a snoozed message", explanation, StringComparison.Ordinal);
        reminder.HtmlBody = reminder.HtmlBody?.Replace("Reminder for a snoozed message", System.Net.WebUtility.HtmlEncode(explanation), StringComparison.Ordinal);
        reminder.FollowUp = new(original.OutgoingMessageId, original.To, sentAt);
        return reminder;
    }

    public static int ReadDraftDays(string rawHeaders)
    {
        foreach (var line in rawHeaders.Split('\n'))
            if (line.StartsWith("X-QuickMail-FollowUp-Days:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), out var days))
                return Math.Clamp(days, 0, 3650);
        return 0;
    }
}
