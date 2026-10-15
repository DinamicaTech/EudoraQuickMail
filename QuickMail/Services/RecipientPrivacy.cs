using MimeKit;

namespace QuickMail.Services;

public enum RecipientPrivacyChoice { Cancel, ProtectWithBcc, KeepVisible }
public sealed record RecipientPrivacyAssessment(int VisibleCount, int DomainCount)
{
    public bool RequiresWarning => VisibleCount > 3 && DomainCount > 1;
}

/// <summary>A privacy heuristic, not a determination that recipients know one another.</summary>
public static class RecipientPrivacy
{
    public static RecipientPrivacyAssessment Assess(string to, string cc)
    {
        var visible = Parse(to).Concat(Parse(cc)).DistinctBy(m => m.Address, StringComparer.OrdinalIgnoreCase).ToList();
        return new(visible.Count, visible.Select(m => m.Address[(m.Address.LastIndexOf('@') + 1)..])
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    public static (string To, string Bcc) Protect(string sender, string to, string cc, string bcc)
    {
        if (!MailboxAddress.TryParse(sender, out var own) || !own.Address.Contains('@'))
            throw new FormatException("The sender must be a valid email address before protecting recipients.");
        // Parse all fields before returning any changes; a malformed BCC must never be silently lost.
        var recipients = Parse(to).Concat(Parse(cc)).Concat(Parse(bcc))
            .DistinctBy(m => m.Address, StringComparer.OrdinalIgnoreCase);
        // Plain addresses are unambiguous for all existing compose/transport parsers.
        return (own.Address, string.Join(", ", recipients.Select(m => m.Address)));
    }

    private static IEnumerable<MailboxAddress> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (InternetAddressList.TryParse(text, out var list)) return list.Mailboxes.ToArray();
        // EQM accepts semicolon-delimited address lists as well as RFC mailbox groups.
        if (!text.Contains(':') && InternetAddressList.TryParse(text.Replace(';', ','), out list))
            return list.Mailboxes.ToArray();
        throw new FormatException("A recipient list could not be read. Review TO, CC and BCC before sending.");
    }
}
