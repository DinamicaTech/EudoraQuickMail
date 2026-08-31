using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Diagnostics;
using MimeKit;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

public sealed class Pop3ReceiveService : IPop3ReceiveService
{
    // Eudora calls its incoming mailbox "In". Local/POP3 mail uses that canonical physical name
    // too; "INBOX" remains untouched for IMAP, where it is a real server folder name.
    public const string InboxFolderName = "In";
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
        var operationStarted = Stopwatch.GetTimestamp();
        long connectTicks = 0, authenticateTicks = 0, uidlTicks = 0, lookupTicks = 0,
            fetchTicks = 0, saveTicks = 0, deleteTicks = 0, quitTicks = 0;
        var downloaded = 0;
        var known = 0;
        var serverMessages = 0;
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
            var phase = Stopwatch.GetTimestamp();
            await transport.ConnectAsync(account, ct);
            connectTicks += Stopwatch.GetElapsedTime(phase).Ticks;
            connected = true;
            phase = Stopwatch.GetTimestamp();
            await transport.AuthenticateAsync(account.AuthUsername, password, ct);
            authenticateTicks += Stopwatch.GetElapsedTime(phase).Ticks;
            if (!transport.SupportsUidListing)
                throw new NotSupportedException("The POP3 server must support UIDL.");

            phase = Stopwatch.GetTimestamp();
            var uidls = await transport.GetMessageUidsAsync(ct);
            uidlTicks += Stopwatch.GetElapsedTime(phase).Ticks;
            serverMessages = uidls.Count;
            var newMessages = new List<MailMessageSummary>();
            for (var index = 0; index < uidls.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var uidl = uidls[index];
                phase = Stopwatch.GetTimestamp();
                var alreadyKnown = await _store.ContainsPop3UidAsync(account.Id, uidl, ct);
                lookupTicks += Stopwatch.GetElapsedTime(phase).Ticks;
                if (alreadyKnown)
                {
                    // A previous QUIT may have failed after local commit. Do not duplicate it;
                    // issue DELE again so this successful session can finish the handoff.
                    phase = Stopwatch.GetTimestamp();
                    await transport.DeleteMessageAsync(index, ct);
                    deleteTicks += Stopwatch.GetElapsedTime(phase).Ticks;
                    known++;
                    continue;
                }

                phase = Stopwatch.GetTimestamp();
                var mime = await transport.GetMessageAsync(index, ct);
                fetchTicks += Stopwatch.GetElapsedTime(phase).Ticks;
                var local = MapMessage(account.Id, uidl, mime, account.Username);
                phase = Stopwatch.GetTimestamp();
                await _store.SavePop3MessageAsync(uidl, local, ct); // durable before DELE
                saveTicks += Stopwatch.GetElapsedTime(phase).Ticks;
                newMessages.Add(local);
                phase = Stopwatch.GetTimestamp();
                await transport.DeleteMessageAsync(index, ct);
                deleteTicks += Stopwatch.GetElapsedTime(phase).Ticks;
                downloaded++;
            }

            phase = Stopwatch.GetTimestamp();
            await transport.DisconnectAsync(commitDeletes: true, ct); // QUIT commits DELE
            quitTicks += Stopwatch.GetElapsedTime(phase).Ticks;
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
            var details = $"account={account.AccountLabel}; server={serverMessages}; downloaded={downloaded}; known={known}";
            PerformanceLogService.Record("POP3: connect", TimeSpan.FromTicks(connectTicks), details);
            PerformanceLogService.Record("POP3: authenticate", TimeSpan.FromTicks(authenticateTicks), details);
            PerformanceLogService.Record("POP3: UIDL listing", TimeSpan.FromTicks(uidlTicks), details);
            PerformanceLogService.Record("POP3: local UID lookups", TimeSpan.FromTicks(lookupTicks), details);
            PerformanceLogService.Record("POP3: download MIME messages", TimeSpan.FromTicks(fetchTicks), details);
            PerformanceLogService.Record("POP3: materialize messages in SQLite", TimeSpan.FromTicks(saveTicks), details);
            PerformanceLogService.Record("POP3: issue server deletes", TimeSpan.FromTicks(deleteTicks), details);
            PerformanceLogService.Record("POP3: commit deletes with QUIT", TimeSpan.FromTicks(quitTicks), details);
            PerformanceLogService.Record("POP3: account total", Stopwatch.GetElapsedTime(operationStarted), details);
        }
    }

    internal static MailMessageDetail MapMessage(Guid accountId, string uidl, MimeMessage message,
        string? fallbackRecipient = null)
    {
        var plain = message.TextBody ?? string.Empty;
        var html = message.HtmlBody ?? string.Empty;
        var previewSource = string.IsNullOrWhiteSpace(plain) ? StripHtml(html) : plain;
        var receivedDate = message.Date.UtcDateTime.Year < 1900
            ? DateTimeOffset.UtcNow
            : message.Date;
        var recipients = message.To.ToString();
        if (string.IsNullOrWhiteSpace(recipients))
            recipients = FirstNonEmptyHeader(message, "Delivered-To", "X-Original-To", "Envelope-To")
                         ?? fallbackRecipient
                         ?? string.Empty;
        // Parse before materializing attachments: decoding a MimeContent may consume the backing
        // stream for messages loaded from POP3. Parsing after ExtractMimeResources therefore saw
        // an empty text/calendar part in real mail even though constructed unit-test parts survived.
        var rawCalendarIcs = CalendarMimeHelper.FindCalendarText(message);
        var attachments = ExtractMimeResources(message);
        var detail = new MailMessageDetail
        {
            MessageId = StableMessageId(accountId, uidl),
            AccountId = accountId,
            FolderName = InboxFolderName,
            InternetMessageId = message.MessageId ?? string.Empty,
            From = message.From.ToString(),
            To = recipients,
            Cc = message.Cc.ToString(),
            Bcc = message.Bcc.ToString(),
            ReplyTo = message.ReplyTo.ToString(),
            Subject = string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject,
            Date = receivedDate,
            IsRead = false,
            Direction = MessageDirection.Incoming,
            Preview = CollapseWhitespace(previewSource, 240),
            PlainTextBody = plain,
            HtmlBody = html,
            RawHeaders = SerializeHeaders(message.Headers),
            Attachments = attachments,
        };
        CalendarMimeHelper.Populate(detail, rawCalendarIcs, "Pop3ReceiveService");
        return detail;
    }

    private static string? FirstNonEmptyHeader(MimeMessage message, params string[] names)
    {
        foreach (var name in names)
        {
            var value = message.Headers[name];
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    private static string SerializeHeaders(HeaderList headers)
    {
        using var stream = new MemoryStream();
        headers.WriteTo(stream, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static List<AttachmentModel> ExtractMimeResources(MimeMessage message)
    {
        var resources = new List<AttachmentModel>();
        foreach (var entity in message.BodyParts)
        {
            if (entity is not MimePart part || part.Content is null) continue;
            var isCalendar = CalendarMimeHelper.IsCalendarPart(part);
            if (!isCalendar && (part.ContentType.IsMimeType("text", "plain") ||
                                part.ContentType.IsMimeType("text", "html"))) continue;

            var isAttachment = part.ContentDisposition?.IsAttachment == true;
            var contentId = part.ContentId?.Trim().Trim('<', '>');
            var isInline = !isAttachment &&
                (part.ContentDisposition?.Disposition?.Equals("inline", StringComparison.OrdinalIgnoreCase) == true ||
                 !string.IsNullOrWhiteSpace(contentId));
            var fileName = part.FileName;
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = isCalendar
                    ? "event.ics"
                    : isInline && !string.IsNullOrWhiteSpace(contentId)
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
