using Microsoft.Data.Sqlite;
using QuickMail.Models;

namespace QuickMail.Services;

public partial class LocalStoreService
{
    private async Task MigrateLegacySnoozedMessagesAsync()
    {
        var legacy = new List<(SnoozedMessageKey Key, DateTimeOffset Wake)>();
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT z.account_id,z.folder_name,z.unique_id,z.wake_ticks
                FROM SnoozedMessage z JOIN MessageSummary s
                  ON s.account_id=z.account_id AND s.folder_name=z.folder_name AND s.unique_id=z.unique_id
                WHERE z.original_folder='' AND z.wake_ticks>$now
                  AND EXISTS(SELECT 1 FROM Folder f WHERE f.account_id=z.account_id
                    AND f.kind=$kind);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
            command.Parameters.AddWithValue("$kind", (int)SpecialFolderKind.Snoozed);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                legacy.Add((new SnoozedMessageKey(Guid.Parse(reader.GetString(0)),
                    reader.GetString(1), reader.GetString(2)),
                    new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero)));
        }
        foreach (var (key, wake) in legacy)
            await SnoozeMessagesAsync([key], wake);
    }

    /// <summary>
    /// Resolves a reminder link after the original message may have moved. The exact stored
    /// location wins; otherwise the RFC Message-ID is stable across moves, with the local id as a
    /// final fallback for imported mail that has no RFC id.
    /// </summary>
    public async Task<MailMessageSummary?> ResolveSnoozeReminderTargetAsync(
        SnoozeReminderTarget target, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT unique_id,account_id,folder_name,internet_message_id,from_disp,to_addr,
                   subject,date_ticks,is_read,preview_text,is_replied,is_forwarded,
                   has_attachments,is_mailing_list,flag_id,message_direction,notes
            FROM MessageSummary
            WHERE account_id=$account AND (
                (folder_name=$folder AND unique_id=$id)
                OR ($internet<>'' AND internet_message_id=$internet)
                OR unique_id=$id)
            ORDER BY CASE
                WHEN folder_name=$folder AND unique_id=$id THEN 0
                WHEN unique_id=$id THEN 1
                ELSE 2 END,
                date_ticks DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$account", target.AccountId.ToString());
        command.Parameters.AddWithValue("$folder", target.FolderName);
        command.Parameters.AddWithValue("$id", target.MessageId);
        command.Parameters.AddWithValue("$internet", target.InternetMessageId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLocalSummary(reader) : null;
    }

    public async Task SnoozeMessagesAsync(IReadOnlyCollection<SnoozedMessageKey> messages,
        DateTimeOffset wakeAtUtc, CancellationToken ct = default)
    {
        if (messages.Count == 0) return;
        await using var connection = await OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var message in messages)
        {
            var parkingFolder = await GetSnoozeFolderAsync(connection, transaction, message.AccountId, ct);
            if (message.FolderName == parkingFolder)
            {
                await using var reschedule = connection.CreateCommand();
                reschedule.Transaction = transaction;
                reschedule.CommandText = """
                    UPDATE SnoozedMessage SET wake_ticks=$wake
                    WHERE account_id=$account AND folder_name=$folder AND unique_id=$id;
                    """;
                reschedule.Parameters.AddWithValue("$account", message.AccountId.ToString());
                reschedule.Parameters.AddWithValue("$folder", parkingFolder);
                reschedule.Parameters.AddWithValue("$id", message.MessageId);
                reschedule.Parameters.AddWithValue("$wake", wakeAtUtc.UtcTicks);
                if (await reschedule.ExecuteNonQueryAsync(ct) == 0)
                    throw new InvalidOperationException("This message is not parked in Snooze.");
                continue;
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO SnoozedMessage(account_id,folder_name,unique_id,wake_ticks,original_folder)
                    SELECT $account,$folder,$id,$wake,$folder
                    WHERE EXISTS(SELECT 1 FROM MessageSummary
                        WHERE account_id=$account AND folder_name=$folder AND unique_id=$id)
                    ON CONFLICT(account_id,folder_name,unique_id) DO UPDATE SET
                        wake_ticks=excluded.wake_ticks,original_folder=excluded.original_folder;
                    """;
                insert.Parameters.AddWithValue("$account", message.AccountId.ToString());
                insert.Parameters.AddWithValue("$folder", message.FolderName);
                insert.Parameters.AddWithValue("$id", message.MessageId);
                insert.Parameters.AddWithValue("$wake", wakeAtUtc.UtcTicks);
                if (await insert.ExecuteNonQueryAsync(ct) == 0)
                    throw new InvalidOperationException("The message to snooze is no longer in its original folder.");
            }
            await MoveSnoozedRecordAsync(connection, transaction, message.AccountId,
                message.FolderName, parkingFolder, message.MessageId, ct);
            await UpdateFolderCountsAsync(connection, transaction, message.AccountId, message.FolderName, ct);
            await UpdateFolderCountsAsync(connection, transaction, message.AccountId, parkingFolder, ct);
        }
        await transaction.CommitAsync(ct);
        foreach (var message in messages)
            await RecordMessageActivityAsync(new MailMessageSummary { AccountId = message.AccountId,
                FolderName = message.FolderName, MessageId = message.MessageId }, "Snoozed", $"Wake at {wakeAtUtc:O}", ct);
    }

    public async Task<IReadOnlyList<MailMessageSummary>> LoadSnoozedMessagesAsync(
        IReadOnlyCollection<Guid>? accountIds = null, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        var accountFilter = accountIds is { Count: > 0 }
            ? $" AND s.account_id IN ({string.Join(',', accountIds.Select((_, index) => "$account" + index))})"
            : string.Empty;
        command.CommandText = $"""
            SELECT s.unique_id,s.account_id,s.folder_name,s.internet_message_id,s.from_disp,s.to_addr,
                   s.subject,z.wake_ticks,s.is_read,s.preview_text,s.is_replied,s.is_forwarded,
                   s.has_attachments,s.is_mailing_list,s.flag_id,s.message_direction,s.notes
            FROM MessageSummary s JOIN SnoozedMessage z
              ON z.account_id=s.account_id AND z.folder_name=s.folder_name AND z.unique_id=s.unique_id
            WHERE 1=1{accountFilter}
            ORDER BY z.wake_ticks, s.date_ticks DESC;
            """;
        if (accountIds is not null)
        {
            var index = 0;
            foreach (var accountId in accountIds) command.Parameters.AddWithValue("$account" + index++, accountId.ToString());
        }
        var result = new List<MailMessageSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadLocalSummary(reader));
        return result;
    }

    public async Task<IReadOnlySet<SnoozedMessageKey>> LoadActiveSnoozedKeysAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id,COALESCE(NULLIF(original_folder,''),folder_name),unique_id FROM SnoozedMessage;";
        var keys = new HashSet<SnoozedMessageKey>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (Guid.TryParse(reader.GetString(0), out var accountId))
                keys.Add(new SnoozedMessageKey(accountId, reader.GetString(1), reader.GetString(2)));
        }
        return keys;
    }

    public async Task<IReadOnlyList<MailMessageSummary>> WakeDueSnoozedMessagesAsync(
        DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var due = new List<(MailMessageSummary Message, string OriginalFolder)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT s.unique_id,s.account_id,s.folder_name,s.internet_message_id,s.from_disp,s.to_addr,
                       s.subject,s.date_ticks,s.is_read,s.preview_text,s.is_replied,s.is_forwarded,
                       s.has_attachments,s.is_mailing_list,s.flag_id,s.message_direction,s.notes,z.original_folder
                FROM MessageSummary s JOIN SnoozedMessage z
                  ON z.account_id=s.account_id AND z.folder_name=s.folder_name AND z.unique_id=s.unique_id
                WHERE z.wake_ticks <= $now;
                """;
            query.Parameters.AddWithValue("$now", nowUtc.UtcTicks);
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                due.Add((ReadLocalSummary(reader), reader.GetString(17)));
        }
        foreach (var (message, originalFolder) in due)
        {
            if (originalFolder.Length > 0)
            {
                await MoveSnoozedRecordAsync(connection, transaction, message.AccountId,
                    message.FolderName, originalFolder, message.MessageId, ct);
                await UpdateFolderCountsAsync(connection, transaction, message.AccountId, message.FolderName, ct);
                message.FolderName = originalFolder;
            }
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE MessageSummary SET is_read=0
                WHERE account_id=$account AND folder_name=$folder AND unique_id=$id;
                DELETE FROM SnoozedMessage
                WHERE account_id=$account AND folder_name=$folder AND unique_id=$id;
                """;
            update.Parameters.AddWithValue("$account", message.AccountId.ToString());
            update.Parameters.AddWithValue("$folder", message.FolderName);
            update.Parameters.AddWithValue("$id", message.MessageId);
            await update.ExecuteNonQueryAsync(ct);
            await UpdateFolderCountsAsync(connection, transaction, message.AccountId, message.FolderName, ct);
        }
        await transaction.CommitAsync(ct);
        foreach (var (message, _) in due)
        {
            message.IsRead = false;
            await RecordMessageActivityAsync(message, "Snooze ended", "Returned to inbox management", ct);
        }
        return due.Select(item => item.Message).ToList();
    }

    public async Task<string?> GetSnoozedOriginalFolderAsync(Guid accountId, string parkedFolder,
        string messageId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT original_folder FROM SnoozedMessage
            WHERE account_id=$account AND folder_name=$folder AND unique_id=$id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$account", accountId.ToString());
        command.Parameters.AddWithValue("$folder", parkedFolder);
        command.Parameters.AddWithValue("$id", messageId);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task<string?> CancelSnoozeAsync(Guid accountId, string parkedFolder,
        string messageId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        string? originalFolder;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = """
                SELECT original_folder FROM SnoozedMessage
                WHERE account_id=$account AND folder_name=$folder AND unique_id=$id LIMIT 1;
                """;
            lookup.Parameters.AddWithValue("$account", accountId.ToString());
            lookup.Parameters.AddWithValue("$folder", parkedFolder);
            lookup.Parameters.AddWithValue("$id", messageId);
            originalFolder = await lookup.ExecuteScalarAsync(ct) as string;
        }
        if (string.IsNullOrEmpty(originalFolder)) return null;
        await MoveSnoozedRecordAsync(connection, transaction, accountId,
            parkedFolder, originalFolder, messageId, ct);
        await using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = """
                DELETE FROM SnoozedMessage WHERE account_id=$account AND folder_name=$folder AND unique_id=$id;
                """;
            remove.Parameters.AddWithValue("$account", accountId.ToString());
            remove.Parameters.AddWithValue("$folder", originalFolder);
            remove.Parameters.AddWithValue("$id", messageId);
            await remove.ExecuteNonQueryAsync(ct);
        }
        await UpdateFolderCountsAsync(connection, transaction, accountId, parkedFolder, ct);
        await UpdateFolderCountsAsync(connection, transaction, accountId, originalFolder, ct);
        await transaction.CommitAsync(ct);
        return originalFolder;
    }

    public async Task<IReadOnlyList<(string Id, DateTimeOffset ReceivedUtc, bool IsRead)>>
        LoadFolderMessageIdDatesUnfilteredAsync(Guid accountId, string folderName,
            CancellationToken ct = default)
    {
        var result = new List<(string, DateTimeOffset, bool)>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT unique_id,date_ticks,is_read FROM MessageSummary
            WHERE account_id=$account AND folder_name=$folder;
            """;
        command.Parameters.AddWithValue("$account", accountId.ToString());
        command.Parameters.AddWithValue("$folder", folderName);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((reader.GetString(0),
                new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), reader.GetInt64(2) != 0));
        return result;
    }

    private static async Task<string> GetSnoozeFolderAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid accountId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT full_name FROM Folder WHERE account_id=$account AND kind=$kind LIMIT 1;";
        command.Parameters.AddWithValue("$account", accountId.ToString());
        command.Parameters.AddWithValue("$kind", (int)SpecialFolderKind.Snoozed);
        return await command.ExecuteScalarAsync(ct) as string
            ?? throw new InvalidOperationException("The Snooze folder has not been initialized for this account.");
    }

    private static async Task MoveSnoozedRecordAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid accountId, string source, string destination,
        string id, CancellationToken ct)
    {
        if (source == destination) return;
        // This move is deliberately local, even for remote accounts. The original server folder
        // remains in SnoozedMessage.original_folder so a server refresh can ignore its copy.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE MessageSummary SET folder_name=$dest
              WHERE account_id=$account AND folder_name=$source AND unique_id=$id;
            UPDATE SnoozedMessage SET folder_name=$dest
              WHERE account_id=$account AND folder_name=$source AND unique_id=$id;
            UPDATE MessageDetail SET folder_name=$dest
              WHERE account_id=$account AND folder_name=$source AND unique_id=$id;
            UPDATE LocalMessageFts SET folder_name=$dest WHERE rowid IN
              (SELECT fts_rowid FROM LocalMessageFtsKey
                WHERE account_id=$account AND folder_name=$source AND unique_id=$id);
            UPDATE LocalMessageFtsKey SET folder_name=$dest
              WHERE account_id=$account AND folder_name=$source AND unique_id=$id;
            UPDATE AttachmentContent SET folder_name=$dest
              WHERE account_id=$account AND folder_name=$source AND unique_id=$id;
            UPDATE ImapBodyCacheState SET folder_name=$dest
              WHERE account_id=$account AND folder_name=$source AND unique_id=$id;
            """;
        command.Parameters.AddWithValue("$account", accountId.ToString());
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$dest", destination);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

}
