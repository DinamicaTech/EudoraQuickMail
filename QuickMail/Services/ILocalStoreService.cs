using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface ILocalStoreService
{
    void Initialize();

    Task UpsertSummariesAsync(IEnumerable<MailMessageSummary> summaries);
    Task<List<MailMessageSummary>> LoadAllSummariesAsync();
    Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId);
    Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null);
    Task DeleteSummariesAsync(Guid accountId, string folderName, IEnumerable<string> messageIds);
    Task DeleteAccountDataAsync(Guid accountId);

    /// <summary>Clears cached mail (summaries, bodies, delta cursors) for the given accounts only —
    /// the one-time Graph immutable-id rebuild (#366). Calendar events are not touched.</summary>
    Task ClearCachedMailAsync(IEnumerable<Guid> accountIds);

    /// <summary>
    /// Deletes calendar events for accounts not in <paramref name="knownAccountIds"/> (orphans from a
    /// removed-and-re-added account or a cache rebuild). Local events (<see cref="Guid.Empty"/>) are kept.
    /// </summary>
    Task PurgeCalendarEventsForUnknownAccountsAsync(IReadOnlyCollection<Guid> knownAccountIds);

    // ── Folders (#516) ───────────────────────────────────────────────────────────
    // The folder list is persisted so the startup folder can be resolved — and the folder tree
    // drawn — before any account connects. Without this, folder metadata exists only in
    // MainViewModel._cachedFolders, populated by GetFoldersAsync after connect, so nothing at
    // launch knows which folders exist or which of them are Inboxes.

    /// <summary>
    /// Replaces the stored folder list for one account, in a single transaction. Replace rather than
    /// upsert: a folder deleted or renamed on the server must disappear locally. Header rows and rows
    /// with an empty <c>FullName</c> are skipped — they are display artifacts, not server folders.
    /// Other accounts are untouched, so a partial connect refreshes only what it reached.
    /// </summary>
    Task SaveFoldersAsync(Guid accountId, IReadOnlyList<MailFolderModel> folders);

    /// <summary>Renames/moves a local folder path and all descendant message/index records.</summary>
    Task RenameFolderPathAsync(Guid accountId, string oldPath, string newPath) =>
        throw new NotSupportedException("Local folder moves are not supported by this store.");

    /// <summary>
    /// Every stored folder, grouped by account and in the order it was last saved. Shaped to drop
    /// straight into <c>MainViewModel._cachedFolders</c>.
    /// </summary>
    Task<Dictionary<Guid, List<MailFolderModel>>> LoadFoldersAsync();

    /// <summary>Ensures every local account has its physical and canonical system folders even
    /// when the account cannot connect (for example, before its password has been entered).</summary>
    Task EnsureLocalSystemFoldersAsync(IReadOnlyCollection<AccountModel> accounts) => Task.CompletedTask;

    /// <summary>Returns the validated account-independent local tree when its shadow migration is
    /// present. Null keeps older/unmigrated profiles on the legacy per-account tree.</summary>
    Task<CanonicalLocalFolderTree?> LoadCanonicalLocalFolderTreeAsync() => Task.FromResult<CanonicalLocalFolderTree?>(null);
    Task<string> EnsureCanonicalFolderBindingAsync(Guid folderId, Guid accountId) =>
        throw new NotSupportedException("Canonical local folders are not supported by this store.");
    Task<Guid> CreateCanonicalFolderAsync(Guid parentFolderId, string name, Guid ownerAccountId,
        bool isContainer = true) =>
        throw new NotSupportedException("Canonical local folders are not supported by this store.");
    Task<CanonicalFolderMoveResult> MoveCanonicalFolderAsync(Guid folderId, Guid newParentFolderId) =>
        throw new NotSupportedException("Canonical local folder moves are not supported by this store.");
    Task<CanonicalFolderMoveResult> RenameCanonicalFolderAsync(Guid folderId, string newName) =>
        throw new NotSupportedException("Canonical local folder renaming is not supported by this store.");
    Task DeleteCanonicalFolderTreeAsync(Guid folderId) =>
        throw new NotSupportedException("Canonical local folder deletion is not supported by this store.");

    /// <summary>
    /// Deletes folders for accounts not in <paramref name="knownAccountIds"/> — orphans left by an
    /// account removed while the app was closed, or removed and re-added under a new id.
    /// </summary>
    Task PurgeFoldersForUnknownAccountsAsync(IReadOnlyCollection<Guid> knownAccountIds);
    Task UpdateIsReadAsync(Guid accountId, string folderName, string messageId, bool isRead);
    Task UpdateIsReadBatchAsync(IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items, bool isRead);
    /// <summary>Updates only the cached sender text for an existing summary. This deliberately
    /// never inserts a row: notification activation may use a key-only temporary summary, and
    /// persisting that object would overwrite the real recipient, subject and date with defaults.</summary>
    Task UpdateSenderAsync(Guid accountId, string folderName, string messageId, string sender) =>
        Task.CompletedTask;
    /// <summary>Records that the user successfully sent a reply to the original local message.</summary>
    Task UpdateIsRepliedAsync(Guid accountId, string folderName, string messageId, string? internetMessageId = null) =>
        Task.CompletedTask;
    Task UpdatePreviewAsync(Guid accountId, string folderName, string messageId, string preview);

    /// <summary>
    /// Batch-update preview text for many messages in one transaction. Used by SyncService
    /// after fetching previews so a folder of N messages doesn't issue N round-trips.
    /// </summary>
    Task UpdatePreviewsBatchAsync(Guid accountId, string folderName, IEnumerable<(string MessageId, string Preview)> updates);
    Task<bool> HasSummariesMissingRecipientsAsync();

    Task UpsertDetailAsync(MailMessageDetail detail);
    Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, string messageId);

    /// <summary>Loads the body and secondary recipients needed while evaluating rules. The default
    /// implementation keeps test/probe stores source-compatible; SQLite overrides it with one
    /// folder-level query so a rule does not perform one database round-trip per message.</summary>
    async Task<IReadOnlyDictionary<string, RuleMatchData>> LoadRuleMatchDataAsync(
        Guid accountId, string folderName, IReadOnlyCollection<string> messageIds,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, RuleMatchData>(StringComparer.Ordinal);
        foreach (var messageId in messageIds)
        {
            ct.ThrowIfCancellationRequested();
            var detail = await LoadDetailAsync(accountId, folderName, messageId);
            if (detail is null) continue;
            result[messageId] = RuleMatchData.From(detail);
        }
        return result;
    }

    /// <summary>
    /// Returns the highest message key stored for this folder, or "0" if none. For the IMAP
    /// backend this is the numeric high-water UID, computed as MAX(CAST(unique_id AS INTEGER))
    /// and rendered as a decimal string; the Graph backend does not use it (it tracks a delta token).
    /// </summary>
    Task<string> GetMaxMessageKeyAsync(Guid accountId, string folderName);

    /// <summary>Returns all message ids stored locally for this folder.</summary>
    Task<HashSet<string>> GetAllMessageIdsAsync(Guid accountId, string folderName);

    /// <summary>
    /// Returns id → is_read for every message stored locally in this folder. The periodic sweep uses it
    /// to reconcile read/unread state changed by another client (#462): it diffs this against the
    /// server's read state from the id listing and updates only the rows that differ, no message fetch.
    /// The key set is also the folder's cached-id set (drives the addition/deletion diff), so this one
    /// query replaces a separate <see cref="GetAllMessageIdsAsync"/> on the sweep path.
    /// </summary>
    Task<Dictionary<string, bool>> LoadFolderReadStatesAsync(Guid accountId, string folderName);

    /// <summary>Which of <paramref name="messageIds"/> already exist in the folder (bounded lookup).</summary>
    Task<HashSet<string>> GetExistingMessageIdsAsync(Guid accountId, string folderName, IEnumerable<string> messageIds);

    /// <summary>
    /// Counts all message summaries stored for the given account.
    /// Returns 0 if the account has no messages or does not exist.
    /// </summary>
    Task<int> CountSummariesAsync(Guid accountId);

    /// <summary>
    /// Cached message-summary count per folder for one account, keyed by folder_name, in a single
    /// grouped scan (#462 sweep instrumentation). One query per account beats one per folder — it keeps
    /// the measurement's own cost off the timed region and off the per-folder hot path.
    /// </summary>
    Task<Dictionary<string, int>> CountSummariesByFolderAsync(Guid accountId);

    /// <summary>
    /// Returns the oldest message date stored for the given account, or null if no messages exist.
    /// Used to display the cache window in Account Properties.
    /// </summary>
    Task<DateTimeOffset?> GetOldestMessageDateAsync(Guid accountId);

    /// <summary>
    /// Sets or clears the named-flag assignment on a single message.
    /// Pass null for flagId to clear the flag.
    /// </summary>
    Task UpdateFlagIdAsync(Guid accountId, string folderName, string messageId, string? flagId);

    /// <summary>
    /// Batch-sets or clears the named-flag assignment on multiple messages in a single transaction.
    /// Pass null for flagId to clear all flags in the batch.
    /// </summary>
    Task UpdateFlagIdBatchAsync(
        IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items,
        string? flagId);

    // ── Calendar events ──────────────────────────────────────────────────────────

    /// <summary>Upserts a calendar event by (Uid, AccountId).</summary>
    Task UpsertCalendarEventAsync(CalendarEvent evt);

    /// <summary>Loads all calendar events, ordered by start time ascending (nulls last).</summary>
    Task<List<CalendarEvent>> LoadCalendarEventsAsync();

    /// <summary>
    /// Returns the distinct server calendars (one row per account + calendar) across all synced rows
    /// (<c>is_graph = 1</c> with a non-empty <c>calendar_id</c>), for building the per-calendar
    /// grandchild nodes under each account in the folder tree.
    /// </summary>
    Task<IReadOnlyList<(Guid AccountId, string CalendarId, string CalendarName)>> LoadCalendarSourcesAsync();

    /// <summary>Updates only the response status for an event.</summary>
    Task UpdateCalendarResponseStatusAsync(string uid, Guid accountId, CalendarResponseStatus status);

    /// <summary>Deletes a calendar event by Uid + AccountId.</summary>
    Task DeleteCalendarEventAsync(string uid, Guid accountId);

    /// <summary>
    /// Replaces all Graph-synced calendar rows (<c>is_graph = 1</c>) for the account with the
    /// supplied fresh set, in one transaction. Rows are stored with <c>is_graph = 1</c> regardless
    /// of each event's flag. Harvested-invite and locally-authored rows are untouched. Used by
    /// <see cref="GraphCalendarSyncService"/>'s replace-slice sync (read-down v1, no delta tokens).
    /// </summary>
    Task ReplaceGraphCalendarEventsAsync(Guid accountId, IReadOnlyList<CalendarEvent> events);

    /// <summary>Removes server-synced events overlapping a UTC range before a focused re-sync.</summary>
    Task DeleteGraphCalendarEventsInRangeAsync(Guid accountId, DateTime startUtc, DateTime endUtc);

    /// <summary>
    /// Returns all non-empty calendar_ics rows from MessageDetail, for harvesting.
    /// Each item is (AccountId, FolderName, MessageId, IcsText).
    /// </summary>
    Task<List<(Guid AccountId, string FolderName, string MessageId, string IcsText)>> LoadAllCalendarIcsAsync();

    /// <summary>
    /// Clears source_message_id and source_folder on any CalendarEvent whose source
    /// message no longer exists in MessageDetail. Called after each harvest so that
    /// events whose invite emails were purged from the local cache don't produce
    /// "message not found" errors when the user tries to open the invitation.
    /// </summary>
    Task ClearOrphanedCalendarSourceLinksAsync();

    /// <summary>
    /// Returns the stored Microsoft Graph delta cursor (a full <c>@odata.deltaLink</c> URL) for an
    /// account+folder, or null if none has been persisted yet (first poll). See dev spec §6.12.
    /// </summary>
    Task<string?> GetDeltaTokenAsync(Guid accountId, string folderId);

    /// <summary>Persists the Graph delta cursor for an account+folder, replacing any existing value.</summary>
    Task SetDeltaTokenAsync(Guid accountId, string folderId, string deltaToken);
}

public sealed record RuleMatchData(string Body, string Cc, string Bcc)
{
    public static RuleMatchData From(MailMessageDetail detail) => new(
        string.IsNullOrWhiteSpace(detail.PlainTextBody) ? detail.HtmlBody ?? string.Empty : detail.PlainTextBody,
        detail.Cc ?? string.Empty,
        detail.Bcc ?? string.Empty);
}
