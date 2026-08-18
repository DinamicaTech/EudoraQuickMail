using MimeKit;
using QuickMail.Models;
using System.IO;

namespace QuickMail.Services;

/// <summary>IMailService facade over the authoritative SQLite store for POP3 and archive accounts.</summary>
public sealed class LocalMailService : IMailService
{
    private readonly LocalStoreService _store;
    private readonly Dictionary<Guid, AccountModel> _accounts = [];

    public LocalMailService(LocalStoreService store) => _store = store;

    public async Task ConnectAsync(AccountModel account, string? password = null, CancellationToken ct = default)
    {
        _accounts[account.Id] = account;
        if (account.BackendKind == BackendKind.Pop3Smtp && (await GetFoldersAsync(account.Id, ct)).Count == 0)
        {
            var root = "Mailbox";
            await _store.SaveFoldersAsync(account.Id,
            [
                new() { AccountId = account.Id, FullName = root, DisplayName = root, IsContainer = true },
                new() { AccountId = account.Id, FullName = "Inbox", DisplayName = "Inbox", ParentId = root, Kind = SpecialFolderKind.Inbox },
                new() { AccountId = account.Id, FullName = "Sent", DisplayName = "Sent", ParentId = root, Kind = SpecialFolderKind.Sent },
                new() { AccountId = account.Id, FullName = "Drafts", DisplayName = "Drafts", ParentId = root, Kind = SpecialFolderKind.Drafts },
                new() { AccountId = account.Id, FullName = "Trash", DisplayName = "Trash", ParentId = root, Kind = SpecialFolderKind.Trash },
            ]);
        }
    }
    public Task DisconnectAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    public bool IsConnected(Guid accountId) => _accounts.ContainsKey(accountId);

    public async Task<List<MailFolderModel>> GetFoldersAsync(Guid accountId, CancellationToken ct = default) =>
        (await _store.LoadFoldersAsync()).GetValueOrDefault(accountId) ?? [];

    public Task<List<MailMessageSummary>> GetMessageSummariesAsync(Guid accountId, string folderName,
        int maxMessages, CancellationToken ct = default) =>
        _store.LoadFolderSummariesAsync(accountId, folderName,
            Math.Min(Math.Max(0, maxMessages), LocalMailConstants.MaxRenderedMessages));

    public async Task<List<MailMessageSummary>> GetMessagesSinceDateAsync(Guid accountId, string folderName,
        DateTime since, CancellationToken ct = default) =>
        (await _store.LoadFolderSummariesAsync(accountId, folderName, LocalMailConstants.MaxRenderedMessages))
        .Where(m => m.Date >= since).ToList();

    public Task<List<MailMessageSummary>> GetMessagesSinceAsync(Guid accountId, string folderName,
        string sinceMessageId, int initialCount, CancellationToken ct = default) =>
        GetMessageSummariesAsync(accountId, folderName, initialCount, ct);

    public async Task<MailMessageDetail> GetMessageDetailAsync(Guid accountId, string folderName,
        string messageId, CancellationToken ct = default) =>
        await _store.LoadDetailAsync(accountId, folderName, messageId)
        ?? throw new KeyNotFoundException("Local message not found.");

    public Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName,
        string messageId, CancellationToken ct = default) => GetMessageDetailAsync(accountId, folderName, messageId, ct);
    public Task MarkReadAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) =>
        _store.UpdateIsReadAsync(accountId, folderName, messageId, true);
    public Task MarkReadBatchAsync(Guid accountId, string folderName, IList<string> messageIds,
        CancellationToken ct = default) => _store.UpdateIsReadBatchAsync(
            messageIds.Select(id => (accountId, folderName, id)), true);
    public Task SetMessageFlaggedAsync(Guid accountId, string folderName, string messageId, bool flagged,
        CancellationToken ct = default) => _store.UpdateFlagIdAsync(accountId, folderName, messageId,
            flagged ? FlagDefinition.BuiltInFlagId.ToString() : null);

    public Task MoveToTrashAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default) =>
        MoveToTrashBatchAsync(accountId, folderName, [messageId], ct);
    public async Task MoveToTrashBatchAsync(Guid accountId, string folderName, IList<string> messageIds,
        CancellationToken ct = default)
    {
        var trash = (await GetFoldersAsync(accountId, ct)).FirstOrDefault(f => f.Kind == SpecialFolderKind.Trash)
            ?? throw new InvalidOperationException("This local account has no Trash folder.");
        await _store.MoveLocalMessagesAsync(accountId, folderName, trash.FullName, messageIds.ToList(), ct);
    }
    public Task PermanentlyDeleteBatchAsync(Guid accountId, string folderName, IList<string> messageIds,
        CancellationToken ct = default) => _store.DeleteLocalMessagesAsync(accountId, folderName, messageIds.ToList(), ct);
    public Task NoOpAsync(Guid accountId, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<int> CountTrashMessagesAsync(Guid accountId, CancellationToken ct = default)
    {
        var trash = (await GetFoldersAsync(accountId, ct)).FirstOrDefault(f => f.Kind == SpecialFolderKind.Trash);
        return trash is null ? 0 : (await _store.LoadFolderSummariesAsync(accountId, trash.FullName)).Count;
    }
    public async Task<int> EmptyTrashAsync(Guid accountId, CancellationToken ct = default)
    {
        var trash = (await GetFoldersAsync(accountId, ct)).FirstOrDefault(f => f.Kind == SpecialFolderKind.Trash);
        if (trash is null) return 0;
        var ids = await _store.GetAllMessageIdsAsync(accountId, trash.FullName);
        await _store.DeleteLocalMessagesAsync(accountId, trash.FullName, ids, ct);
        return ids.Count;
    }
    public async Task<IList<string>> GetFolderMessageIdsAsync(Guid accountId, string folderName,
        CancellationToken ct = default) => (await _store.GetAllMessageIdsAsync(accountId, folderName)).ToList();
    public async Task<IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)>> GetFolderMessageIdDatesAsync(
        Guid accountId, string folderName, CancellationToken ct = default) =>
        (await _store.LoadFolderSummariesAsync(accountId, folderName))
        .Select(m => (m.MessageId, m.Date, m.IsRead)).ToList();
    public async Task<IReadOnlyDictionary<string, string>> FetchPreviewsAsync(Guid accountId, string folderName,
        IList<string> messageIds, int maxLines, CancellationToken ct = default) =>
        (await _store.LoadFolderSummariesAsync(accountId, folderName))
        .Where(m => messageIds.Contains(m.MessageId)).ToDictionary(m => m.MessageId, m => m.Preview);
    public Task<int> PollAsync(Guid accountId, string folderName, CancellationToken ct = default) => Task.FromResult(0);
    public async Task<(int Total, int Unread)> GetInboxStatusAsync(Guid accountId, CancellationToken ct = default)
    {
        var inbox = (await GetFoldersAsync(accountId, ct)).FirstOrDefault(f => f.Kind == SpecialFolderKind.Inbox);
        if (inbox is null) return (0, 0);
        var messages = await _store.LoadFolderSummariesAsync(accountId, inbox.FullName);
        return (messages.Count, messages.Count(m => !m.IsRead));
    }
    public async Task<string?> FindDraftsFolderNameAsync(Guid accountId, CancellationToken ct = default) =>
        (await GetFoldersAsync(accountId, ct)).FirstOrDefault(f => f.Kind == SpecialFolderKind.Drafts)?.FullName;

    public async Task<string> AppendDraftAsync(Guid accountId, ComposeModel draft, string? replaceMessageId,
        CancellationToken ct = default)
    {
        var folder = await FindDraftsFolderNameAsync(accountId, ct)
            ?? throw new InvalidOperationException("This local account has no Drafts folder.");
        if (replaceMessageId is not null)
            await _store.DeleteLocalMessagesAsync(accountId, folder, [replaceMessageId], ct);
        var id = "local-" + Guid.NewGuid().ToString("N");
        await _store.SaveLocalMessageAsync(FromCompose(accountId, folder, id, draft), ct);
        return id;
    }

    public async Task AppendToSentAsync(Guid accountId, ComposeModel sent, CancellationToken ct = default)
    {
        var folder = (await GetFoldersAsync(accountId, ct)).FirstOrDefault(f => f.Kind == SpecialFolderKind.Sent)
            ?? throw new InvalidOperationException("This local account has no Sent folder.");
        await _store.SaveLocalMessageAsync(FromCompose(accountId, folder.FullName,
            "local-" + Guid.NewGuid().ToString("N"), sent), ct);
    }

    private MailMessageDetail FromCompose(Guid accountId, string folder, string id, ComposeModel compose)
    {
        var account = _accounts.GetValueOrDefault(accountId)
            ?? throw new InvalidOperationException("Local account is not connected.");
        return new MailMessageDetail
        {
            AccountId = accountId, FolderName = folder, MessageId = id, From = account.Username,
            To = compose.To, Cc = compose.Cc, Subject = compose.Subject, Date = DateTimeOffset.UtcNow,
            PlainTextBody = compose.Body, Preview = compose.Body.Length <= 240 ? compose.Body : compose.Body[..240],
            IsRead = true,
        };
    }

    public async Task<byte[]> DownloadAttachmentAsync(Guid accountId, string folderName, string messageId,
        string partSpecifier, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(partSpecifier))
            throw new InvalidOperationException("The imported attachment reference is not an absolute path.");
        if (!File.Exists(partSpecifier))
            throw new FileNotFoundException("The original Eudora attachment is no longer at its imported location.", partSpecifier);
        return await File.ReadAllBytesAsync(partSpecifier, ct);
    }
    public Task CopyMessagesAsync(Guid accountId, string folderName, IList<string> messageIds,
        string destinationFolder, CancellationToken ct = default) =>
        throw new NotSupportedException("Local message copy is not part of the MVP; use move.");
    public Task MoveMessagesAsync(Guid accountId, string folderName, IList<string> messageIds,
        string destinationFolder, CancellationToken ct = default) =>
        _store.MoveLocalMessagesAsync(accountId, folderName, destinationFolder, messageIds.ToList(), ct);

    public async Task CreateFolderAsync(Guid accountId, string? parentFolderName, string name,
        CancellationToken ct = default)
    {
        var all = await GetFoldersAsync(accountId, ct);
        var fullName = string.IsNullOrEmpty(parentFolderName) ? name : parentFolderName + "/" + name;
        if (all.Any(f => f.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A folder with that name already exists.");
        all.Add(new MailFolderModel
        {
            AccountId = accountId, FullName = fullName, DisplayName = name, ParentId = parentFolderName,
            IsContainer = string.IsNullOrEmpty(parentFolderName),
        });
        await _store.SaveFoldersAsync(accountId, all);
    }
    public Task DeleteFolderAsync(Guid accountId, string folderName, CancellationToken ct = default) =>
        throw new NotSupportedException("Local folder deletion will be enabled after the empty-folder guard is wired.");
    public Task RenameFolderAsync(Guid accountId, string folderName, string newName,
        string? newParentFolderName, CancellationToken ct = default) =>
        throw new NotSupportedException("Local folder rename is not yet enabled.");
    public Task CopyFolderAsync(Guid accountId, string folderName, string? destinationParentName,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Local folder copy is not part of the MVP.");
    public void Dispose() { }
}
