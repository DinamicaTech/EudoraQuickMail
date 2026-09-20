using Microsoft.Data.Sqlite;

namespace QuickMail.Services;

public partial class LocalStoreService
{
    public async Task<IReadOnlyList<string>> TrackAndGetExpiredRecoveryDeletedAsync(
        Guid accountId, string folderName, IReadOnlyCollection<string> currentMessageIds,
        DateTimeOffset observedUtc, DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        if (currentMessageIds.Count == 0) return [];

        await using var connection = await OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var messageId in currentMessageIds)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO RecoveryDeletedState
                    (account_id,folder_name,unique_id,first_seen_ticks)
                VALUES($account,$folder,$id,$seen);
                """;
            insert.Parameters.AddWithValue("$account", accountId.ToString("D"));
            insert.Parameters.AddWithValue("$folder", folderName);
            insert.Parameters.AddWithValue("$id", messageId);
            insert.Parameters.AddWithValue("$seen", observedUtc.UtcTicks);
            await insert.ExecuteNonQueryAsync(ct);
        }

        var current = currentMessageIds.ToHashSet(StringComparer.Ordinal);
        var expired = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT unique_id FROM RecoveryDeletedState
                 WHERE account_id=$account AND folder_name=$folder AND first_seen_ticks<=$cutoff;
                """;
            select.Parameters.AddWithValue("$account", accountId.ToString("D"));
            select.Parameters.AddWithValue("$folder", folderName);
            select.Parameters.AddWithValue("$cutoff", cutoffUtc.UtcTicks);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                if (current.Contains(id)) expired.Add(id);
            }
        }
        await transaction.CommitAsync(ct);
        return expired;
    }

    public async Task ForgetRecoveryDeletedAsync(Guid accountId, string folderName,
        IReadOnlyCollection<string> messageIds, CancellationToken ct = default)
    {
        if (messageIds.Count == 0) return;
        await using var connection = await OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var messageId in messageIds)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM RecoveryDeletedState
                 WHERE account_id=$account AND folder_name=$folder AND unique_id=$id;
                """;
            delete.Parameters.AddWithValue("$account", accountId.ToString("D"));
            delete.Parameters.AddWithValue("$folder", folderName);
            delete.Parameters.AddWithValue("$id", messageId);
            await delete.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
