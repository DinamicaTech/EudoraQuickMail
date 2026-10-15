using System.IO;
using System.Net;
using System.Text;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

public sealed class RuleForwardService(IMailService mail, IAccountService accounts, ScheduledSendService queue)
{
    public const string MarkerHeader = "X-QuickMail-Rule-Forward";

    public static string ValidateDestination(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\r') || value.Contains('\n') ||
            !MailboxAddress.TryParse(value, out var address) || !address.Address.Contains('@'))
            throw new ArgumentException("Forward requires one valid destination email address.");
        return address.Address;
    }

    public async Task ForwardAsync(MailRule rule, MailMessageSummary source, CancellationToken ct)
    {
        var target = ValidateDestination(rule.ForwardTo);
        var configured = accounts.LoadAccounts();
        var account = configured.FirstOrDefault(x => x.Id == source.AccountId && x.IsActive)
            ?? throw new InvalidOperationException("The original account is missing or inactive.");
        if (configured.Any(x => string.Equals(x.Username, target, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Automatic forwarding to one of your own configured accounts is blocked to prevent loops.");
        var detail = await mail.GetMessageDetailAsync(source.AccountId, source.FolderName, source.MessageId, ct);
        if (detail.BodyFetchFailed) throw new IOException("Cannot load the original message for forwarding.");
        using var headers = new MemoryStream(Encoding.UTF8.GetBytes(detail.RawHeaders + "\r\n\r\n"));
        using var mime = MimeMessage.Load(headers, ct);
        if (mime.Headers.Contains(MarkerHeader) || mime.Headers["Auto-Submitted"]?.StartsWith("auto-replied", StringComparison.OrdinalIgnoreCase) == true)
            return;
        var keySource = string.Join("|", rule.Id, source.AccountId,
            string.IsNullOrWhiteSpace(source.InternetMessageId) ? source.FolderName + ":" + source.MessageId : source.InternetMessageId,
            target.ToLowerInvariant());
        var intro = $"Forwarded message\nFrom: {detail.From}\nTo: {detail.To}\nDate: {detail.Date:g}\nSubject: {detail.Subject}\n\n";
        var htmlIntro = "<pre>" + WebUtility.HtmlEncode(intro) + "</pre>";
        var bodyTag = System.Text.RegularExpressions.Regex.Match(detail.HtmlBody, @"<body\b[^>]*>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
        var html = bodyTag.Success ? detail.HtmlBody.Insert(bodyTag.Index + bodyTag.Length, htmlIntro) : htmlIntro + detail.HtmlBody;
        var model = new ComposeModel
        {
            AccountId = account.Id, To = target, Subject = detail.Subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) ? detail.Subject : "Fwd: " + detail.Subject,
            Body = intro + (string.IsNullOrEmpty(detail.PlainTextBody) ? Helpers.HtmlStripper.ToPlainText(detail.HtmlBody) : detail.PlainTextBody),
            HtmlBody = string.IsNullOrWhiteSpace(detail.HtmlBody) ? null : html,
            Mode = string.IsNullOrWhiteSpace(detail.HtmlBody) ? ComposeMode.PlainText : ComposeMode.Html,
            RuleForwardKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(keySource))),
        };
        foreach (var attachment in detail.Attachments)
        {
            var content = attachment.Content;
            if (content == null && attachment.PartSpecifier is { } part)
                content = Path.IsPathFullyQualified(part) ? await File.ReadAllBytesAsync(part, ct)
                    : await mail.DownloadAttachmentAsync(source.AccountId, source.FolderName, source.MessageId, part, ct);
            model.Attachments.Add(new AttachmentModel { FileName = attachment.FileName, ContentType = attachment.ContentType,
                ContentId = attachment.ContentId, IsInline = attachment.IsInline, Content = content, FileSize = content?.LongLength ?? attachment.FileSize });
        }
        OutgoingAttachmentValidator.ValidateAndLoad(model);
        await queue.QueueRuleForwardAsync(model, ct);
    }
}
