using Microsoft.Data.Sqlite;
using QuickMail.Models;

namespace QuickMail.Services;

public partial class LocalStoreService
{
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
                   has_attachments,is_mailing_list,flag_id,message_direction
            FROM MessageSummary
            WHERE account_id=$account AND (
                (folder_name=$folder AND unique_id=$id)
                OR ($internet<>'' AND internet_message_id=$internet)
                OR unique_id=$id)
            ORDER BY CASE
                WHEN folder_name=$folder AND unique_id=$id THEN 0
                WHEN $internet<>'' AND internet_message_id=$internet THEN 1
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
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO SnoozedMessage(account_id,folder_name,unique_id,wake_ticks)
            VALUES($account,$folder,$id,$wake)
            ON CONFLICT(account_id,folder_name,unique_id) DO UPDATE SET wake_ticks=excluded.wake_ticks;
            """;
        var account = command.Parameters.Add("$account", SqliteType.Text);
        var folder = command.Parameters.Add("$folder", SqliteType.Text);
        var id = command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.AddWithValue("$wake", wakeAtUtc.UtcTicks);
        foreach (var message in messages)
        {
            account.Value = message.AccountId.ToString();
            folder.Value = message.FolderName;
            id.Value = message.MessageId;
            await command.ExecuteNonQueryAsync(ct);
        }
        await RefreshSnoozeCountsAsync(connection, transaction, ct);
        await transaction.CommitAsync(ct);
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
                   s.has_attachments,s.is_mailing_list,s.flag_id,s.message_direction
            FROM MessageSummary s JOIN SnoozedMessage z
              ON z.account_id=s.account_id AND z.folder_name=s.folder_name AND z.unique_id=s.unique_id
            WHERE z.wake_ticks>$now{accountFilter}
            ORDER BY z.wake_ticks, s.date_ticks DESC;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
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

    public async Task<IReadOnlyList<MailMessageSummary>> WakeDueSnoozedMessagesAsync(
        DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var messages = new List<MailMessageSummary>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT s.unique_id,s.account_id,s.folder_name,s.internet_message_id,s.from_disp,s.to_addr,
                       s.subject,s.date_ticks,s.is_read,s.preview_text,s.is_replied,s.is_forwarded,
                       s.has_attachments,s.is_mailing_list,s.flag_id,s.message_direction
                FROM MessageSummary s JOIN SnoozedMessage z
                  ON z.account_id=s.account_id AND z.folder_name=s.folder_name AND z.unique_id=s.unique_id
                WHERE z.wake_ticks <= $now;
                """;
            query.Parameters.AddWithValue("$now", nowUtc.UtcTicks);
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) messages.Add(ReadLocalSummary(reader));
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE MessageSummary SET is_read=0 WHERE EXISTS(
                  SELECT 1 FROM SnoozedMessage z WHERE z.account_id=MessageSummary.account_id
                    AND z.folder_name=MessageSummary.folder_name AND z.unique_id=MessageSummary.unique_id
                    AND z.wake_ticks <= $now);
                DELETE FROM SnoozedMessage WHERE wake_ticks <= $now;
                """;
            update.Parameters.AddWithValue("$now", nowUtc.UtcTicks);
            await update.ExecuteNonQueryAsync(ct);
        }
        await RefreshSnoozeCountsAsync(connection, transaction, ct);
        await transaction.CommitAsync(ct);
        foreach (var message in messages) message.IsRead = false;
        return messages;
    }

    private static async Task RefreshSnoozeCountsAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken ct)
    {
        await using var source = connection.CreateCommand();
        source.Transaction = transaction;
        source.CommandText = """
            UPDATE Folder SET
              message_count=(SELECT COUNT(*) FROM MessageSummary s WHERE s.account_id=Folder.account_id AND s.folder_name=Folder.full_name
                AND NOT EXISTS(SELECT 1 FROM SnoozedMessage z WHERE z.account_id=s.account_id AND z.folder_name=s.folder_name AND z.unique_id=s.unique_id AND z.wake_ticks>$now)),
              unread_count=(SELECT COUNT(*) FROM MessageSummary s WHERE s.account_id=Folder.account_id AND s.folder_name=Folder.full_name AND s.is_read=0
                AND NOT EXISTS(SELECT 1 FROM SnoozedMessage z WHERE z.account_id=s.account_id AND z.folder_name=s.folder_name AND z.unique_id=s.unique_id AND z.wake_ticks>$now))
            WHERE kind<>$snoozed;
            UPDATE Folder SET
              message_count=(SELECT COUNT(*) FROM SnoozedMessage z JOIN MessageSummary s
                ON s.account_id=z.account_id AND s.folder_name=z.folder_name AND s.unique_id=z.unique_id
                WHERE z.account_id=Folder.account_id AND z.wake_ticks>$now),
              unread_count=0
            WHERE kind=$snoozed;
            """;
        source.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
        source.Parameters.AddWithValue("$snoozed", (int)SpecialFolderKind.Snoozed);
        await source.ExecuteNonQueryAsync(ct);
    }
}
