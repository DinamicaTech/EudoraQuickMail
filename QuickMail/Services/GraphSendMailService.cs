using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services.Graph;

namespace QuickMail.Services;

/// <summary>
/// Send path for Microsoft Graph accounts. Posts a MIME message to <c>/me/sendMail</c>
/// (base64-encoded, <c>Content-Type: text/plain</c>); Graph delivers it and auto-saves a copy
/// to the Sent folder, so <see cref="IMailService.AppendToSentAsync"/> is a no-op for Graph.
/// </summary>
public class GraphSendMailService : ISendMailService, IDisposable
{
    private readonly GraphClient _client;

    /// <param name="http">Optional injected HttpClient for tests; null uses a real one.</param>
    public GraphSendMailService(IOAuthService oauth, HttpClient? http = null) => _client = new GraphClient(oauth, http);

    public async Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default)
    {
        var details = $"account={account.Username}; attachments={compose.Attachments.Count}";
        MimeMessage message;
        using (PerformanceLogService.Measure("Send Graph: build MIME", details))
            message = MimeMessageBuilder.Build(compose, account, MimeMessageBuilder.AppUserAgent);
        await SendMimeAsync(account, message, ct);
    }

    public Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password,
        string organizerEmail, CancellationToken ct = default)
        => SendMimeAsync(account, MimeMessageBuilder.BuildIcsReply(account, icsReplyContent, organizerEmail), ct);

    /// <summary>
    /// No-op for Graph. There is no separate outgoing server to probe: sending uses the same token
    /// and endpoint the mail backend's <c>GET /me</c> connectivity check already exercises, so
    /// Test Connection verifies this path through <see cref="IMailService.ConnectAsync"/>.
    /// </summary>
    public Task VerifyAsync(AccountModel account, string? password, CancellationToken ct = default)
        => Task.CompletedTask;

    private async Task SendMimeAsync(AccountModel account, MimeMessage message, CancellationToken ct)
    {
        // Graph /sendMail takes the MIME message base64-encoded as a text/plain body.
        byte[] body;
        using (PerformanceLogService.Measure("Send Graph: serialize and base64 MIME",
                   $"account={account.Username}"))
            body = await MimeMessageBuilder.ToBase64BytesAsync(message, ct);
        LogService.Log($"GraphSendMailService: sending {body.Length} base64 bytes via /me/sendMail");
        using (PerformanceLogService.Measure("Send Graph: HTTP POST /me/sendMail",
                   $"account={account.Username}; encodedBytes={body.Length}"))
            await _client.PostRawAsync(account, "/me/sendMail", body, "text/plain", ct);
        LogService.Log("GraphSendMailService: send complete");
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
