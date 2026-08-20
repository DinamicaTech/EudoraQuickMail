using System.Net.Sockets;
using QuickMail.Models;

namespace QuickMail.Services;

public sealed record MailOperationFailure(AccountModel Account, string Operation, Exception Error);

public static class MailOperationError
{
    public static string Describe(string operation, AccountModel account, Exception error)
    {
        var root = Root(error);
        var detail = root.Message.Trim();
        var auth = root.GetType().Name.Contains("Authentication", StringComparison.OrdinalIgnoreCase) ||
                   detail.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                   detail.Contains("authenticat", StringComparison.OrdinalIgnoreCase) ||
                   detail.Contains("credentials", StringComparison.OrdinalIgnoreCase);
        var timeout = root is TimeoutException or OperationCanceledException ||
                      detail.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                      detail.Contains("timeout", StringComparison.OrdinalIgnoreCase);
        var socket = root as SocketException;
        var server = operation.Contains("POP3", StringComparison.OrdinalIgnoreCase)
            ? $"{account.Pop3Host}:{account.Pop3Port}"
            : operation.Contains("IMAP", StringComparison.OrdinalIgnoreCase)
                ? $"{account.ImapHost}:{account.ImapPort}"
                : $"{account.SmtpHost}:{account.SmtpPort}";

        var action = auth
            ? "Open File > Manage Accounts, select this account, click Edit, enter the correct password, save it, and use Test Connection. Gmail, Yahoo and iCloud may require an app-specific password."
            : socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData
                ? "Check the server name in File > Manage Accounts and verify that the computer has Internet access."
                : socket?.SocketErrorCode == SocketError.ConnectionRefused
                    ? "Check the server address, port and encryption settings in File > Manage Accounts; the server refused the connection."
                    : timeout
                        ? "Check the Internet connection and the server address/port, then use Test Connection in File > Manage Accounts."
                        : detail.Contains("certificate", StringComparison.OrdinalIgnoreCase) || detail.Contains("SSL", StringComparison.OrdinalIgnoreCase) || detail.Contains("TLS", StringComparison.OrdinalIgnoreCase)
                            ? "Check the encryption mode, server name and system date/time. Do not disable certificate validation unless the server administrator confirms it is necessary."
                            : "Review the account's server, port, encryption, username and password in File > Manage Accounts, then use Test Connection.";

        return $"Account: {account.AccountLabel}\nOperation: {operation}\nServer: {server}\n\nError: {detail}\n\nHow to fix it: {action}";
    }

    private static Exception Root(Exception error)
    {
        while (error.InnerException is { } inner) error = inner;
        return error;
    }
}
