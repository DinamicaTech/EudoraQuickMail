using MailKit.Net.Pop3;
using MailKit.Security;
using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

public sealed class MailKitPop3TransportFactory : IPop3TransportFactory
{
    public IPop3Transport Create(AccountModel account) => new MailKitPop3Transport(account.Pop3AcceptInvalidCert);
}

internal sealed class MailKitPop3Transport : IPop3Transport
{
    private readonly Pop3Client _client = new();

    public MailKitPop3Transport(bool acceptInvalidCertificate)
    {
        if (!acceptInvalidCertificate) return;
#pragma warning disable CA5359
        _client.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
    }

    public bool SupportsUidListing => _client.SupportsUids;

    public Task ConnectAsync(AccountModel account, CancellationToken ct) => _client.ConnectAsync(
        account.Pop3Host, account.Pop3Port,
        account.Pop3UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);

    public Task AuthenticateAsync(string username, string password, CancellationToken ct) =>
        _client.AuthenticateAsync(username, password, ct);

    public async Task<IReadOnlyList<string>> GetMessageUidsAsync(CancellationToken ct) =>
        (await _client.GetMessageUidsAsync(ct)).ToList();

    public Task<MimeMessage> GetMessageAsync(int index, CancellationToken ct) => _client.GetMessageAsync(index, ct);
    public Task DeleteMessageAsync(int index, CancellationToken ct) => _client.DeleteMessageAsync(index, ct);
    public Task DisconnectAsync(bool commitDeletes, CancellationToken ct) => _client.DisconnectAsync(commitDeletes, ct);

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
