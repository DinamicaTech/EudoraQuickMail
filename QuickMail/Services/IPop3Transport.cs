using MimeKit;
using QuickMail.Models;

namespace QuickMail.Services;

public interface IPop3TransportFactory
{
    IPop3Transport Create(AccountModel account);
}

public interface IPop3Transport : IAsyncDisposable
{
    bool SupportsUidListing { get; }
    Task ConnectAsync(AccountModel account, CancellationToken ct);
    Task AuthenticateAsync(string username, string password, CancellationToken ct);
    Task<IReadOnlyList<string>> GetMessageUidsAsync(CancellationToken ct);
    Task<MimeMessage> GetMessageAsync(int index, CancellationToken ct);
    Task DeleteMessageAsync(int index, CancellationToken ct);
    Task DisconnectAsync(bool commitDeletes, CancellationToken ct);
}
