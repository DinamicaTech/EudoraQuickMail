using System.Security.Cryptography;
using System.Text;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

public sealed class Pop3ReceiveService : IPop3ReceiveService
{
    public const string InboxFolderName = "Inbox";
    private readonly IPop3TransportFactory _transportFactory;
    private readonly ILocalMailboxStore _store;
    private readonly IAccountSecretProtector _secrets;

    public Pop3ReceiveService(IPop3TransportFactory transportFactory, ILocalMailboxStore store,
        IAccountSecretProtector secrets)
    {
        _transportFactory = transportFactory;
        _store = store;
        _secrets = secrets;
    }

    public Task<Pop3ReceiveResult> CheckAutomaticallyAsync(AccountModel account, CancellationToken ct = default) =>
        !account.CheckIncomingMail
            ? Task.FromResult(new Pop3ReceiveResult(0, 0, 0, Skipped: true))
            : CheckNowAsync(account, ct);

    public async Task<Pop3ReceiveResult> CheckNowAsync(AccountModel account, CancellationToken ct = default)
    {
        if (account.BackendKind != BackendKind.Pop3Smtp)
            throw new InvalidOperationException("Only POP3/SMTP accounts can check POP3 mail.");
        if (string.IsNullOrWhiteSpace(account.Pop3Host)) throw new InvalidOperationException("POP3 host is required.");
        var password = _secrets.GetPop3Password(account)
            ?? throw new InvalidOperationException("The POP3 password is missing or cannot be decrypted.");

        await using var transport = _transportFactory.Create(account);
        var connected = false;
        var committed = false;
        try
        {
            await transport.ConnectAsync(account, ct);
            connected = true;
            await transport.AuthenticateAsync(account.AuthUsername, password, ct);
            if (!transport.SupportsUidListing)
                throw new NotSupportedException("The POP3 server must support UIDL.");

            var uidls = await transport.GetMessageUidsAsync(ct);
            var downloaded = 0;
            var known = 0;
            var newMessages = new List<MailMessageSummary>();
            for (var index = 0; index < uidls.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var uidl = uidls[index];
                if (await _store.ContainsPop3UidAsync(account.Id, uidl, ct))
                {
                    // A previous QUIT may have failed after local commit. Do not duplicate it;
                    // issue DELE again so this successful session can finish the handoff.
                    await transport.DeleteMessageAsync(index, ct);
                    known++;
                    continue;
                }

                var mime = await transport.GetMessageAsync(index, ct);
                var local = MapMessage(account.Id, uidl, mime);
                await _store.SavePop3MessageAsync(uidl, local, ct); // durable before DELE
                newMessages.Add(local);
                await transport.DeleteMessageAsync(index, ct);
                downloaded++;
            }

            await transport.DisconnectAsync(commitDeletes: true, ct); // QUIT commits DELE
            committed = true;
            connected = false;
            return new Pop3ReceiveResult(downloaded, known, uidls.Count, Skipped: false, newMessages);
        }
        finally
        {
            password = string.Empty;
            if (connected && !committed)
            {
                try { await transport.DisconnectAsync(commitDeletes: false, CancellationToken.None); }
                catch { /* original failure wins; uncommitted DELE is safe to retry */ }
            }
        }
    }

    internal static MailMessageDetail MapMessage(Guid accountId, string uidl, MimeMessage message)
    {
        var plain = message.TextBody ?? string.Empty;
        var html = message.HtmlBody ?? string.Empty;
        var previewSource = string.IsNullOrWhiteSpace(plain) ? StripHtml(html) : plain;
        return new MailMessageDetail
        {
            MessageId = StableMessageId(accountId, uidl),
            AccountId = accountId,
            FolderName = InboxFolderName,
            InternetMessageId = message.MessageId ?? string.Empty,
            From = message.From.ToString(),
            To = message.To.ToString(),
            Cc = message.Cc.ToString(),
            ReplyTo = message.ReplyTo.ToString(),
            Subject = message.Subject ?? "(no subject)",
            Date = message.Date == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : message.Date,
            IsRead = false,
            Preview = CollapseWhitespace(previewSource, 240),
            PlainTextBody = plain,
            HtmlBody = html,
            RawHeaders = message.Headers.ToString() ?? string.Empty,
            Attachments = ExtractMimeResources(message),
        };
    }

    private static List<AttachmentModel> ExtractMimeResources(MimeMessage message)
    {
        var resources = new List<AttachmentModel>();
        foreach (var entity in message.BodyParts)
        {
            if (entity is not MimePart part || part.Content is null) continue;
            if (part.ContentType.IsMimeType("text", "plain") ||
                part.ContentType.IsMimeType("text", "html") ||
                part.ContentType.IsMimeType("text", "calendar")) continue;

            var isAttachment = part.ContentDisposition?.IsAttachment == true;
            var contentId = part.ContentId?.Trim().Trim('<', '>');
            var isInline = !isAttachment &&
                (part.ContentDisposition?.Disposition?.Equals("inline", StringComparison.OrdinalIgnoreCase) == true ||
                 !string.IsNullOrWhiteSpace(contentId));
            var fileName = part.FileName;
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = isInline && !string.IsNullOrWhiteSpace(contentId)
                    ? contentId
                    : "attachment." + part.ContentType.MediaSubtype;

            using var stream = new MemoryStream();
            part.Content.DecodeTo(stream);
            resources.Add(new AttachmentModel
            {
                FileName = fileName,
                ContentType = part.ContentType.MimeType,
                FileSize = stream.Length,
                Content = stream.ToArray(),
                ContentId = contentId,
                IsInline = isInline,
            });
        }
        return resources;
    }

    private static string StableMessageId(Guid accountId, string uidl)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(accountId.ToString("N") + "\0" + uidl));
        return "pop3-" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string StripHtml(string html) => System.Net.WebUtility.HtmlDecode(
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " "));

    private static string CollapseWhitespace(string value, int max)
    {
        var collapsed = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
        return collapsed.Length <= max ? collapsed : collapsed[..max];
    }
}
