using System.Net.Sockets;

namespace QuickMail.Services;

internal static class ConnectionTestTrace
{
    private static readonly AsyncLocal<Action<string>?> Current = new();
    internal static IDisposable Begin(Action<string> report)
    {
        var previous = Current.Value;
        string? lastStage = null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        void Flush()
        {
            if (!string.IsNullOrEmpty(lastStage)) PerformanceLogService.Record("Connection test phase", watch.Elapsed, lastStage);
            watch.Restart();
        }
        Current.Value = stage => { Flush(); lastStage = stage; report(stage); };
        return new Scope(() => { Flush(); Current.Value = previous; });
    }
    internal static void Report(string stage) => Current.Value?.Invoke(stage);
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }

    internal static string Describe(Exception error, string stage, TimeSpan elapsed, string? password)
    {
        var chain = new List<Exception>();
        for (var ex = error; ex != null && chain.Count < 8; ex = ex.InnerException) chain.Add(ex);
        var socket = chain.OfType<SocketException>().FirstOrDefault();
        var detail = string.Join("\n", chain.Select(e => e is MailKit.Net.Smtp.SmtpCommandException smtp
            ? $"SMTP response {(int)smtp.StatusCode} ({smtp.StatusCode}, {smtp.ErrorCode}): {smtp.Message}"
            : $"{e.GetType().Name}: {e.Message}").Distinct());
        if (!string.IsNullOrEmpty(password)) detail = detail.Replace(password, "[redacted]", StringComparison.Ordinal);
        if (detail.Length > 1600) detail = detail[..1600];
        var hint = socket?.SocketErrorCode switch
        {
            SocketError.TimedOut => "The server did not respond in time. Check host/port, firewall, VPN and server availability. This does not establish that the password is wrong.",
            SocketError.ConnectionRefused => "The remote endpoint refused the connection. Check that SMTP is listening on this port and is permitted by the firewall.",
            SocketError.HostNotFound or SocketError.NoData => "DNS could not resolve the configured server. Check the hostname and DNS connection.",
            _ => error is OperationCanceledException ? "The test reached its time limit or was cancelled." :
                chain.Any(e => e is MailKit.Security.SslHandshakeException || e is System.Security.Authentication.AuthenticationException)
                ? "TLS negotiation failed. Check the configured TLS mode, server certificate and system clock; do not disable certificate validation as a workaround." :
                chain.Any(e => e is MailKit.Security.AuthenticationException)
                ? "The server rejected authentication. Check the login username, password/app password or OAuth authorization, and whether SMTP authentication is enabled." : ""
        };
        return $"failed — {stage}\nElapsed: {elapsed.TotalSeconds:F1} s\n" +
            (socket == null ? "" : $"Socket: {socket.SocketErrorCode} (native {socket.NativeErrorCode})\n") + detail +
            (hint.Length == 0 ? "" : "\n\n" + hint);
    }
}
