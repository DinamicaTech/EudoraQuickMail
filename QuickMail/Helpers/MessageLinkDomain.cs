using System.Globalization;
using MimeKit;

namespace QuickMail.Helpers;

/// <summary>Compares a hovered message link with the sender's mail domain.</summary>
internal static class MessageLinkDomain
{
    internal static bool IsForeign(string? url, string? from)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return false;
        if (!MailboxAddress.TryParse(from, out var mailbox)) return false;

        var at = mailbox.Address.LastIndexOf('@');
        if (at < 0 || at == mailbox.Address.Length - 1) return false;

        var sender = NormalizeHost(mailbox.Address[(at + 1)..]);
        var target = NormalizeHost(uri.IdnHost);
        if (sender.Length == 0 || target.Length == 0) return false;

        // Treat a host and any of its subdomains as the same mail domain in either direction.
        // This covers sender.example.com -> example.com and example.com -> www.example.com,
        // while example.com.evil.test remains foreign because of the required dot boundary.
        return !string.Equals(sender, target, StringComparison.OrdinalIgnoreCase)
            && !sender.EndsWith('.' + target, StringComparison.OrdinalIgnoreCase)
            && !target.EndsWith('.' + sender, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHost(string value)
    {
        var host = value.Trim().TrimEnd('.');
        try { return new IdnMapping().GetAscii(host).ToLowerInvariant(); }
        catch (ArgumentException) { return host.ToLowerInvariant(); }
    }
}
