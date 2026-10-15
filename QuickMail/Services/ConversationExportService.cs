using System.IO;
using System.Net;
using System.Text;
using MimeKit;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Services;

public sealed record ConversationExportResult(string Directory, int Exported, int Failed, bool Cancelled);

/// <summary>Portable, inert index plus full EMLs and native attachments. Never overwrites existing exports.</summary>
public sealed class ConversationExportService(ILocalStoreService store, IMailService mail)
{
    public async Task<ConversationExportResult> ExportAsync(IReadOnlyList<MailMessageSummary> messages, string destination,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var directory = Path.Combine(destination, "EQM-export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var rows = new StringBuilder();
        var success = 0; var failed = 0; var cancelled = false;
        for (var i = 0; i < messages.Count; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            var summary = messages[i];
            var itemName = (i + 1).ToString("D5");
            var itemDirectory = Path.Combine(directory, itemName);
            progress?.Report($"Exporting {i + 1:N0}/{messages.Count:N0}: {summary.Subject}");
            try
            {
                MailMessageDetail? detail = null;
                try { detail = await store.LoadDetailAsync(summary.AccountId, summary.FolderName, summary.MessageId); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { LogService.Log("Export: local detail unavailable; trying mail service", ex); }
                ct.ThrowIfCancellationRequested();
                detail ??= await mail.PrefetchMessageDetailAsync(summary.AccountId, summary.FolderName, summary.MessageId, ct);
                if (detail.BodyFetchFailed) throw new IOException("Message body could not be downloaded.");
                Directory.CreateDirectory(itemDirectory);
                var eml = Path.Combine(itemDirectory, "message.eml");
                await MessageExportService.WriteEmlAsync(detail, eml, mail, ct, requireAllAttachments: true);
                using var mime = await MimeMessage.LoadAsync(eml, ct);
                var links = new StringBuilder();
                var partNumber = 0;
                foreach (var part in mime.BodyParts.OfType<MimePart>().Where(p => p.Content != null && (p.IsAttachment || p.ContentId != null)))
                {
                    var name = (++partNumber).ToString("D3") + "-" + AttachmentSafety.SanitizeFileName(part.FileName ?? "embedded.bin");
                    await using var file = new FileStream(Path.Combine(itemDirectory, name), FileMode.CreateNew);
                    await part.Content!.DecodeToAsync(file, ct);
                    links.Append($"<li><a href=\"{itemName}/{Uri.EscapeDataString(name)}\">{Encode(part.FileName ?? "Embedded resource")}</a></li>");
                }
                rows.Append($"<tr><td>{Encode(summary.Date.ToLocalTime().ToString("g"))}</td><td>{Encode(summary.From)}</td><td>{Encode(summary.To)}</td><td><a href=\"{itemName}/message.eml\">{Encode(summary.Subject)}</a></td><td><ul>{links}</ul></td><td>OK</td></tr>");
                success++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; break; }
            catch (Exception ex)
            {
                failed++;
                rows.Append($"<tr><td>{Encode(summary.Date.ToLocalTime().ToString("g"))}</td><td>{Encode(summary.From)}</td><td></td><td>{Encode(summary.Subject)}</td><td></td><td>Incomplete: {Encode(ex.Message)}</td></tr>");
            }
        }
        // No message-supplied HTML, scripts or remote resources run in the index.
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>QuickMail export</title>" +
            "<style>body{font:15px system-ui;margin:24px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #aaa;padding:8px;text-align:left;vertical-align:top}ul{padding-left:18px}</style></head><body>" +
            $"<h1>QuickMail export</h1><p>{success} complete; {failed} incomplete. {(cancelled ? "Cancelled; remaining messages were not exported." : "")}</p>" +
            "<p>Open EML links with an email application. Private Notes and activity logs are not exported.</p><table><thead><tr><th>Date</th><th>From</th><th>To</th><th>Message</th><th>Attachments</th><th>Status</th></tr></thead><tbody>" + rows + "</tbody></table></body></html>";
        await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), html, Encoding.UTF8, CancellationToken.None);
        return new(directory, success, failed, cancelled);
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");
}
