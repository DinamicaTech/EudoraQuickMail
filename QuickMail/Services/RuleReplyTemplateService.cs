using System.IO;
using System.Text;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Conservative automatic replies. Uses the durable forward queue ledger to avoid loops and duplicates.</summary>
public sealed class RuleReplyTemplateService(IMailService mail, IAccountService accounts,
    ITemplateService templates, ScheduledSendService queue)
{
    private static readonly string[] BulkPrecedence = ["bulk", "list", "junk"];
    private static readonly string[] AutomatedLocalParts = ["mailer-daemon", "postmaster", "noreply", "no-reply", "donotreply"];
    public static bool IsAutomaticOrBulk(MimeMessage message)
    {
        var auto = message.Headers["Auto-Submitted"];
        var precedence = message.Headers["Precedence"];
        return message.Headers.Contains(RuleForwardService.MarkerHeader) ||
            (!string.IsNullOrWhiteSpace(auto) && !auto.Equals("no", StringComparison.OrdinalIgnoreCase)) ||
            message.Headers.Contains("List-Id") || message.Headers.Contains("List-Unsubscribe") ||
            message.Headers["Return-Path"]?.Trim() == "<>" ||
            BulkPrecedence.Contains(precedence, StringComparer.OrdinalIgnoreCase) ||
            message.Headers.Contains("X-Auto-Response-Suppress");
    }

    public async Task ReplyAsync(MailRule rule, MailMessageSummary source, CancellationToken ct)
    {
        if (rule.IsExpired || source.Direction == MessageDirection.Outgoing) return;
        var configured = accounts.LoadAccounts();
        var account = configured.FirstOrDefault(a => a.Id == source.AccountId && a.IsActive)
            ?? throw new InvalidOperationException("The receiving account is missing or inactive.");
        var template = (await templates.LoadAllAsync()).FirstOrDefault(t => t.Id == rule.ReplyTemplateId)
            ?? throw new InvalidOperationException("The reply template no longer exists. Select another template in Rule Manager.");
        var detail = await mail.GetMessageDetailAsync(source.AccountId, source.FolderName, source.MessageId, ct);
        if (detail.BodyFetchFailed) throw new IOException("Cannot read the message headers for a safe automatic reply.");
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(detail.RawHeaders + "\r\n\r\n"));
        using var mime = MimeMessage.Load(input, ct);
        if (IsAutomaticOrBulk(mime)) return;
        var recipient = string.IsNullOrWhiteSpace(detail.ReplyTo) ? detail.From : detail.ReplyTo;
        if (!MailboxAddress.TryParse(recipient, out var address) || !address.Address.Contains('@')) return;
        var target = address.Address;
        var localPart = target.Split('@')[0];
        if (configured.Any(a => a.Username.Equals(target, StringComparison.OrdinalIgnoreCase)) ||
            AutomatedLocalParts.Contains(localPart, StringComparer.OrdinalIgnoreCase)) return;
        // At most one reply per sender, rule and configured expiration period (vacation), not per incoming message.
        var key = $"reply|{rule.Id}|{source.AccountId}|{target.ToLowerInvariant()}|{rule.ActiveUntil:yyyyMMdd}";
        var now = DateTime.Now;
        string Expand(string text) => text
            .Replace("{sender}", string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("d"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("t"), StringComparison.OrdinalIgnoreCase);
        await queue.QueueRuleForwardAsync(new ComposeModel
        {
            AccountId = account.Id, To = target,
            Subject = string.IsNullOrWhiteSpace(template.Subject)
                ? (detail.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? detail.Subject : "Re: " + detail.Subject)
                : Expand(template.Subject),
            Body = Expand(template.Body), Mode = ComposeMode.PlainText,
            InReplyToMessageId = string.IsNullOrWhiteSpace(detail.InternetMessageId)
                ? source.InternetMessageId : detail.InternetMessageId,
            RuleForwardKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key))),
        }, ct);
    }
}
