using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
        var template = (await templates.LoadAllAsync()).FirstOrDefault(t => t.Id == rule.ReplyTemplateId)
            ?? throw new InvalidOperationException("The reply template no longer exists. Select another template in Rule Manager.");
        var prepared = await PrepareReplyAsync(rule, source, ct);
        if (prepared is not { } reply) return;
        var (account, detail, target) = reply;
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
            RuleForwardKey = HashKey(key),
        }, ct);
    }

    public async Task AutoresponseAsync(MailRule rule, MailMessageSummary source, CancellationToken ct)
    {
        var prepared = await PrepareReplyAsync(rule, source, ct);
        if (prepared is not { } reply) return;
        var (account, detail, target) = reply;
        var originalBody = string.IsNullOrWhiteSpace(detail.PlainTextBody)
            ? Helpers.HtmlStripper.ToPlainText(detail.HtmlBody) : detail.PlainTextBody;
        string Expand(string value) => Regex.Replace(value, @"\[SUBJECT\]|\[BODY\]", match =>
            match.Value == "[SUBJECT]" ? detail.Subject : originalBody);
        var subject = Expand(rule.AutoResponseSubject ?? "[SUBJECT]")
            .Replace('\r', ' ').Replace('\n', ' ');
        var body = Expand(rule.AutoResponseBody ?? "[BODY]");
        var originalId = string.IsNullOrWhiteSpace(detail.InternetMessageId)
            ? source.InternetMessageId : detail.InternetMessageId;
        var identity = string.IsNullOrWhiteSpace(originalId)
            ? source.FolderName + ":" + source.MessageId : originalId;
        var key = $"autoresponse|{rule.Id}|{source.AccountId}|{identity}|{target.ToLowerInvariant()}";
        await queue.QueueRuleForwardAsync(new ComposeModel
        {
            AccountId = account.Id, To = target, Subject = subject, Body = body,
            Mode = ComposeMode.PlainText, InReplyToMessageId = originalId,
            RuleForwardKey = HashKey(key),
        }, ct);
    }

    private static string HashKey(string key) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private async Task<(AccountModel Account, MailMessageDetail Detail, string Target)?> PrepareReplyAsync(
        MailRule rule, MailMessageSummary source, CancellationToken ct)
    {
        if (rule.IsExpired || source.Direction == MessageDirection.Outgoing) return null;
        var configured = accounts.LoadAccounts();
        var account = configured.FirstOrDefault(a => a.Id == source.AccountId && a.IsActive)
            ?? throw new InvalidOperationException("The receiving account is missing or inactive.");
        var detail = await mail.PrefetchMessageDetailAsync(source.AccountId, source.FolderName, source.MessageId, ct);
        if (detail.BodyFetchFailed) throw new IOException("Cannot read the message headers for a safe automatic reply.");
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(detail.RawHeaders + "\r\n\r\n"));
        using var mime = MimeMessage.Load(input, ct);
        if (IsAutomaticOrBulk(mime)) return null;
        var recipient = string.IsNullOrWhiteSpace(detail.ReplyTo) ? detail.From : detail.ReplyTo;
        if (!MailboxAddress.TryParse(recipient, out var address) || !address.Address.Contains('@')) return null;
        var target = address.Address;
        var localPart = target.Split('@')[0];
        if (configured.Any(a => a.Username.Equals(target, StringComparison.OrdinalIgnoreCase)) ||
            AutomatedLocalParts.Contains(localPart, StringComparer.OrdinalIgnoreCase)) return null;
        return (account, detail, target);
    }
}
