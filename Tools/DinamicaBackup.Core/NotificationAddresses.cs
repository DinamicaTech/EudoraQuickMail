using MimeKit;

namespace DinamicaBackup;

public static class NotificationAddresses
{
    public static void NormalizeLegacySender(NotificationOptions options)
    {
        if (!MailboxAddress.TryParse(options.From, out var sender)) return;
        if (string.IsNullOrWhiteSpace(options.SenderDisplayName)) options.SenderDisplayName = sender.Name ?? "";
        options.From = sender.Address;
    }

    public static MailboxAddress Sender(NotificationOptions options)
    {
        if (!MailboxAddress.TryParse(options.From, out var sender) || !HasDomain(sender.Address))
            throw new InvalidDataException("Sender email must contain a complete email address, for example user@example.com. Put the sending alias in Sender display name (optional).");
        return new MailboxAddress(string.IsNullOrWhiteSpace(options.SenderDisplayName) ? sender.Name : options.SenderDisplayName.Trim(), sender.Address);
    }

    public static InternetAddressList Recipients(NotificationOptions options)
    {
        if (!InternetAddressList.TryParse(options.To, out var recipients) ||
            !recipients.Mailboxes.Any() || recipients.Mailboxes.Any(m => !HasDomain(m.Address)))
            throw new InvalidDataException("Report recipients must contain complete email addresses separated by commas, for example one@example.com, two@example.com.");
        return recipients;
    }

    private static bool HasDomain(string address)
    {
        var at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1;
    }
}
