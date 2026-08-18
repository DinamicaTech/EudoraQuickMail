using QuickMail.Models;

namespace QuickMail.Services;

public sealed record Pop3ReceiveResult(int Downloaded, int RemovedAlreadyKnown, int ServerMessageCount, bool Skipped,
    IReadOnlyList<MailMessageSummary>? NewMessages = null);

public interface IPop3ReceiveService
{
    Task<Pop3ReceiveResult> CheckAutomaticallyAsync(AccountModel account, CancellationToken ct = default);
    Task<Pop3ReceiveResult> CheckNowAsync(AccountModel account, CancellationToken ct = default);
}
