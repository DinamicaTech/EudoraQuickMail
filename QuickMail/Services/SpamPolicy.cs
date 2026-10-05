using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Explicit user blocks only. Never infer spam from message content.</summary>
internal static class SpamPolicy
{
    private static readonly HashSet<string> SharedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "hotmail.com", "hotmail.es", "hotmail.co.uk",
        "outlook.com", "outlook.es", "live.com", "live.es", "msn.com",
        "yahoo.com", "yahoo.es", "yahoo.co.uk", "yahoo.fr", "yahoo.de", "ymail.com",
        "rocketmail.com", "icloud.com", "me.com", "mac.com", "aol.com",
        "proton.me", "protonmail.com", "pm.me", "gmx.com", "gmx.net", "gmx.de",
        "mail.com", "zoho.com", "yandex.com", "yandex.ru", "qq.com", "163.com",
        "fastmail.com", "hey.com", "tutanota.com", "tuta.com", "mail.ru",
        "orange.fr", "wanadoo.fr", "telefonica.net", "terra.es", "privaterelay.appleid.com"
    };

    internal static string Address(string value) =>
        MailboxAddress.TryParse(value, out var mailbox)
            ? mailbox.Address.Trim().ToLowerInvariant() : string.Empty;

    internal static string Domain(string address) => address.Contains('@')
        ? address[(address.LastIndexOf('@') + 1)..].ToLowerInvariant() : string.Empty;

    internal static bool CanBlockDomain(string domain) =>
        Uri.CheckHostName(domain) == UriHostNameType.Dns &&
        domain.Contains('.') && !domain.Contains('@') && !domain.Contains('*') &&
        !System.Text.RegularExpressions.Regex.IsMatch(domain,
            @"^(hotmail|outlook|live|yahoo|gmx)\.(co\.|com\.)?[a-z]{2,3}$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
        !SharedDomains.Any(shared => domain.Equals(shared, StringComparison.OrdinalIgnoreCase)
            || domain.EndsWith("." + shared, StringComparison.OrdinalIgnoreCase));

    internal static bool Matches(MailRule rule, MailMessageSummary message)
    {
        if (message.Direction == MessageDirection.Outgoing) return false;
        var address = Address(message.From);
        if (address.Length == 0) return false;
        return rule.SpamBlockDomain
            ? CanBlockDomain(rule.FromContains ?? "") &&
              Domain(address).Equals(rule.FromContains, StringComparison.OrdinalIgnoreCase)
            : address.Equals(rule.FromContains, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Validate(MailRule rule)
    {
        if (!rule.IsSpamFilter) return;
        var value = rule.FromContains?.Trim().ToLowerInvariant() ?? "";
        if (rule.SpamBlockDomain ? !CanBlockDomain(value) : Address(value) != value || value.Length == 0)
            throw new InvalidOperationException("Invalid SPAM block. Shared mail providers cannot be blocked by domain.");
        rule.FromContains = value;
        rule.AccountId = null;
        rule.ApplyAutomatically = true;
        rule.AlsoFilterOutMailbox = false;
        rule.UseFromCondition = true;
        rule.UseToCondition = rule.UseSubjectCondition = rule.UseBodyCondition = rule.MustHaveAttachments = false;
        rule.Action = RuleAction.MoveToFolder;
        rule.TargetFolder = "Junk";
        rule.AlsoMarkAsRead = false;
    }
}
