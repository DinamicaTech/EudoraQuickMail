using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

public class SmtpService : ISendMailService
{
    private readonly IOAuthService _oauth;
    private readonly ISendMailService _graphSmtp;
    private readonly IAccountSecretProtector? _accountSecrets;

    public SmtpService(IOAuthService oauth, ISendMailService graphSmtp)
        : this(oauth, graphSmtp, null)
    {
    }

    public SmtpService(IOAuthService oauth, ISendMailService graphSmtp, IAccountSecretProtector? accountSecrets)
    {
        _oauth = oauth;
        _graphSmtp = graphSmtp;
        _accountSecrets = accountSecrets;
    }

    public async Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default)
    {
        var details = BuildPerformanceDetails(compose, account);
        var totalStarted = Stopwatch.GetTimestamp();
        var stage = "dispatch";
        var outcome = "failed";
        PerformanceLogService.Marker("Send transport: START", details);
        try
        {
            if (account.BackendKind == BackendKind.MicrosoftGraph)
            {
                stage = "Graph dispatch";
                using (PerformanceLogService.Measure("Send transport: Graph dispatch", details))
                    await _graphSmtp.SendAsync(compose, account, password, ct);
                outcome = "accepted";
                return;
            }

            stage = "credential lookup";
            using (PerformanceLogService.Measure("Send SMTP: credential lookup", details))
                password ??= account.BackendKind == BackendKind.Pop3Smtp
                    ? _accountSecrets?.GetSmtpPassword(account)
                    : null;

            stage = "MIME build";
            MimeMessage message;
            using (PerformanceLogService.Measure("Send SMTP: build MIME", details))
                message = MimeMessageBuilder.Build(compose, account, MimeMessageBuilder.AppUserAgent);

            using var client = new SmtpClient();

            if (account.SmtpAcceptInvalidCert)
            {
#pragma warning disable CA5359 // callback intentionally accepts any cert when the user enables SmtpAcceptInvalidCert
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
            }

            var ssl = MailSecurity.ForSmtp(account);
            var connectionDetails = $"{details}; host={account.SmtpHost}; port={account.SmtpPort}; security={ssl}";

            try
            {
                stage = "connect/TLS";
                LogService.Log($"SmtpService: connecting to {account.SmtpHost}:{account.SmtpPort} ssl={ssl}");
                using (PerformanceLogService.Measure("Send SMTP: connect and negotiate TLS", connectionDetails))
                    await client.ConnectAsync(account.SmtpHost, account.SmtpPort, ssl, ct);
                LogService.Log($"SmtpService: connected to {account.SmtpHost}:{account.SmtpPort}");

                stage = "authenticate";
                if (account.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google)
                {
                    LogService.Debug($"SmtpService: authenticating via XOAUTH2");
                    string token;
                    stage = "OAuth token";
                    using (PerformanceLogService.Measure("Send SMTP: acquire OAuth token", details))
                        token = await _oauth.GetAccessTokenAsync(account, ct);
                    stage = "authenticate";
                    using (PerformanceLogService.Measure("Send SMTP: authenticate", details))
                        await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, token), ct);
                }
                else
                {
                    using (PerformanceLogService.Measure("Send SMTP: authenticate", details))
                        await client.AuthenticateAsync(account.AuthUsername, password!, ct);
                }
                LogService.Log($"SmtpService: authenticated, sending.");
                stage = "submit message";
                using (PerformanceLogService.Measure("Send SMTP: submit message", details))
                    await client.SendAsync(message, ct);
                outcome = "accepted";
                LogService.Log($"SmtpService: send complete");
            }
            catch (Exception ex)
            {
                PerformanceLogService.Marker("Send SMTP: FAILED",
                    $"{connectionDetails}; stage={stage}; error={ex.GetType().Name}");
                LogService.Log($"SmtpService: send failed ({ex.GetType().Name})", ex);
                throw;
            }

            // Outside the inner try, and swallowing its own failure: the message is ACCEPTED by the
            // server at this point, so a QUIT that throws is not a send failure. Keep it separately
            // timed: some servers delay their final QUIT response even though delivery is complete.
            stage = "disconnect";
            using (PerformanceLogService.Measure("Send SMTP: disconnect", details))
                await DisconnectQuietlyAsync(client, ct);
            outcome = "completed";
        }
        finally
        {
            PerformanceLogService.Record("Send transport: total", Stopwatch.GetElapsedTime(totalStarted),
                $"{details}; outcome={outcome}; lastStage={stage}");
        }
    }

    private static string BuildPerformanceDetails(ComposeModel compose, AccountModel account)
    {
        var attachmentBytes = compose.Attachments.Sum(attachment =>
            attachment.Content?.LongLength ?? Math.Max(0, attachment.FileSize));
        return $"account={account.Username}; backend={account.BackendKind}; auth={account.AuthType}; " +
               $"mode={compose.Mode}; bodyChars={compose.Body.Length}; htmlChars={compose.HtmlBody?.Length ?? 0}; " +
               $"attachments={compose.Attachments.Count}; attachmentBytes={attachmentBytes}";
    }

    /// <summary>
    /// Best-effort QUIT. Never throws: every caller has already completed the work that matters.
    /// </summary>
    private static async Task DisconnectQuietlyAsync(SmtpClient client, CancellationToken ct)
    {
        try
        {
            await client.DisconnectAsync(true, ct);
        }
        catch (OperationCanceledException)
        {
            // The send's own timeout expiring during QUIT. Not a failure of anything — logged
            // separately so the log does not report a cancellation as a disconnect error.
            LogService.Debug("SmtpService: disconnect cancelled after the work completed.");
        }
        catch (Exception ex)
        {
            LogService.Log($"SmtpService: disconnect after send failed, ignoring ({ex.GetType().Name})", ex);
        }
    }

    public async Task VerifyAsync(AccountModel account, string? password, CancellationToken ct = default)
    {
        if (account.BackendKind == BackendKind.MicrosoftGraph)
        {
            await _graphSmtp.VerifyAsync(account, password, ct);
            return;
        }

        password ??= account.BackendKind == BackendKind.Pop3Smtp ? _accountSecrets?.GetSmtpPassword(account) : null;
        using var client = new SmtpClient();

        if (account.SmtpAcceptInvalidCert)
        {
#pragma warning disable CA5359 // callback intentionally accepts any cert when the user enables SmtpAcceptInvalidCert
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        // Same SSL and SASL selection as SendAsync — a verification that connected differently from
        // the real send would prove nothing.
        var ssl = MailSecurity.ForSmtp(account);

        LogService.Log($"SmtpService: verifying {account.SmtpHost}:{account.SmtpPort} ssl={ssl}");
        await client.ConnectAsync(account.SmtpHost, account.SmtpPort, ssl, ct);

        if (account.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google)
        {
            var token = await _oauth.GetAccessTokenAsync(account, ct);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, token), ct);
        }
        else
        {
            await client.AuthenticateAsync(account.AuthUsername, password!, ct);
        }

        // Nothing is sent — authenticating is the whole proof, so a failing QUIT must not turn a
        // successful verification into "Test Connection failed".
        await DisconnectQuietlyAsync(client, ct);
        LogService.Log("SmtpService: verify succeeded");
    }

    public async Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password,
        string organizerEmail, CancellationToken ct = default)
    {
        if (account.BackendKind == BackendKind.MicrosoftGraph)
        {
            await _graphSmtp.SendIcsReplyAsync(icsReplyContent, account, password, organizerEmail, ct);
            return;
        }

        password ??= account.BackendKind == BackendKind.Pop3Smtp ? _accountSecrets?.GetSmtpPassword(account) : null;
        var message = MimeMessageBuilder.BuildIcsReply(account, icsReplyContent, organizerEmail);

        using var client = new SmtpClient();

        if (account.SmtpAcceptInvalidCert)
        {
#pragma warning disable CA5359 // callback intentionally accepts any cert when the user enables SmtpAcceptInvalidCert
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        var ssl = MailSecurity.ForSmtp(account);

        try
        {
            LogService.Log($"SmtpService: sending ICS reply to {account.SmtpHost}:{account.SmtpPort}");
            await client.ConnectAsync(account.SmtpHost, account.SmtpPort, ssl, ct);

            if (account.AuthType is AuthType.OAuth2Microsoft or AuthType.OAuth2Google)
            {
                var token = await _oauth.GetAccessTokenAsync(account, ct);
                await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, token), ct);
            }
            else
            {
                await client.AuthenticateAsync(account.AuthUsername, password!, ct);
            }
            LogService.Log($"SmtpService: ICS reply authenticated, sending.");
            await client.SendAsync(message, ct);
            LogService.Log($"SmtpService: ICS reply sent.");
        }
        catch (Exception ex)
        {
            LogService.Log($"SmtpService: ICS reply send failed ({ex.GetType().Name})", ex);
            throw;
        }

        // See SendAsync: the reply has been accepted, so a failing QUIT must not surface as a
        // failure to respond to the invitation.
        await DisconnectQuietlyAsync(client, ct);
    }
}
