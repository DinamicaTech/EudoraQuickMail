using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Authoritative operations for POP3 and local-archive accounts.</summary>
public interface ILocalMailboxStore
{
    Task<bool> ContainsPop3UidAsync(Guid accountId, string uidl, CancellationToken ct = default);
    Task SavePop3MessageAsync(string uidl, MailMessageDetail message, CancellationToken ct = default);
    Task SaveLocalMessageAsync(MailMessageDetail message, CancellationToken ct = default);
    Task<LocalSearchResult> SearchLocalMessagesAsync(LocalSearchQuery query, CancellationToken ct = default);
    Task<LocalSearchResult> SearchLocalMessagesAdvancedAsync(AdvancedSearchQuery query, CancellationToken ct = default) =>
        throw new NotSupportedException("Advanced local search is not implemented by this store.");
    Task<LocalSearchResult> LoadLocalPageAsync(Guid? accountId, string? folderName, int limit, int offset,
        LocalSearchSort sort = LocalSearchSort.NewestFirst, bool includeDescendants = false,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Paged local loading is not implemented by this store.");
    Task MoveLocalMessagesAsync(Guid accountId, string sourceFolder, string destinationFolder,
        IReadOnlyCollection<string> messageIds, CancellationToken ct = default);
    Task DeleteLocalMessagesAsync(Guid accountId, string folderName,
        IReadOnlyCollection<string> messageIds, CancellationToken ct = default);
}
