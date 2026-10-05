using Microsoft.Data.Sqlite;
using QuickMail.Models;

namespace QuickMail.Services;

public partial class LocalStoreService
{
    private static void InitializeSpamTriggers(SqliteConnection connection)
    {
        // Legacy migrations rebuild MessageSummary (and drop its triggers). Install afterwards,
        // including on a completely new profile, so local identities survive the first move.
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER IF NOT EXISTS trg_spam_move_identity
            AFTER UPDATE OF folder_name,unique_id ON MessageSummary BEGIN
                UPDATE OR REPLACE SpamMessageState
                SET identity='local:' || NEW.folder_name || char(31) || NEW.unique_id
                WHERE account_id=OLD.account_id
                  AND identity='local:' || OLD.folder_name || char(31) || OLD.unique_id;
                DELETE FROM SpamJunkAge WHERE account_id=OLD.account_id
                  AND folder_name=OLD.folder_name AND unique_id=OLD.unique_id;
            END;
            CREATE TRIGGER IF NOT EXISTS trg_spam_delete_age
            AFTER DELETE ON MessageSummary BEGIN
                DELETE FROM SpamJunkAge WHERE account_id=OLD.account_id
                  AND folder_name=OLD.folder_name AND unique_id=OLD.unique_id;
                DELETE FROM SpamMessageState WHERE account_id=OLD.account_id
                  AND identity='local:' || OLD.folder_name || char(31) || OLD.unique_id;
            END;
            CREATE TRIGGER IF NOT EXISTS trg_spam_reset_age_identity
            AFTER UPDATE OF internet_message_id ON MessageSummary
            WHEN OLD.internet_message_id IS NOT NEW.internet_message_id BEGIN
                DELETE FROM SpamJunkAge WHERE account_id=OLD.account_id
                  AND folder_name=OLD.folder_name AND unique_id=OLD.unique_id;
                DELETE FROM SpamMessageState WHERE account_id=OLD.account_id
                  AND identity='local:' || OLD.folder_name || char(31) || OLD.unique_id;
            END;
            """;
        command.ExecuteNonQuery();
    }
    internal async Task<MailMessageSummary?> LoadExactSpamCandidateAsync(Guid account, string folder, string id,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT unique_id,account_id,folder_name,internet_message_id,from_disp,to_addr,
                   subject,date_ticks,is_read,preview_text,is_replied,is_forwarded,
                   has_attachments,is_mailing_list,flag_id,message_direction
            FROM MessageSummary WHERE account_id=$a AND folder_name=$f AND unique_id=$id
            """;
        command.Parameters.AddWithValue("$a", account.ToString());
        command.Parameters.AddWithValue("$f", folder);
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLocalSummary(reader) : null;
    }
    internal async Task<bool> OfferSpamPromptAsync(MailMessageSummary message, CancellationToken ct = default)
    {
        var address = SpamPolicy.Address(message.From);
        if (address.Length == 0 || message.IsOutgoing) return false;
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT offered FROM SpamSenderPrompt WHERE address=$address";
        command.Parameters.AddWithValue("$address", address);
        if (await command.ExecuteScalarAsync(ct) is not null) return false;
        command.CommandText = """
            SELECT unique_id,account_id,folder_name,internet_message_id,from_disp
            FROM MessageSummary WHERE instr(lower(from_disp),$address)>0
            """;
        var known = false;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (SpamPolicy.Address(reader.GetString(4)) != address) continue;
                var same = !reader.IsDBNull(3) && !string.IsNullOrEmpty(message.InternetMessageId)
                    && reader.GetString(3) == message.InternetMessageId;
                same |= reader.GetString(0) == message.MessageId &&
                    reader.GetString(1) == message.AccountId.ToString() && reader.GetString(2) == message.FolderName;
                if (!same) { known = true; break; }
            }
        }
        command.CommandText = "INSERT OR IGNORE INTO SpamSenderPrompt(address,offered) VALUES($address,1)";
        var inserted = await command.ExecuteNonQueryAsync(ct);
        return inserted == 1 && !known;
    }
    // Internet identity survives server UID changes; folder/UID fallback never conflates unrelated mail.
    internal const string SpamPredicate = """
        EXISTS(SELECT 1 FROM SpamMessageState spam WHERE spam.account_id=s.account_id AND
        spam.identity=CASE WHEN COALESCE(s.internet_message_id,'')<>'' THEN 'mid:' || s.internet_message_id
        ELSE 'local:' || s.folder_name || char(31) || s.unique_id END)
        """;
    private static string SpamIdentity(MailMessageSummary message) =>
        string.IsNullOrEmpty(message.InternetMessageId)
            ? "local:" + message.FolderName + '' + message.MessageId
            : "mid:" + message.InternetMessageId;

    internal async Task SetSpamAsync(MailMessageSummary message, bool spam, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = spam
            ? "INSERT OR IGNORE INTO SpamMessageState(account_id,identity,classified_ticks) VALUES($a,$i,$t)"
            : "DELETE FROM SpamMessageState WHERE account_id=$a AND identity=$i";
        command.Parameters.AddWithValue("$a", message.AccountId.ToString());
        command.Parameters.AddWithValue("$i", SpamIdentity(message));
        if (spam) command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.UtcTicks);
        await command.ExecuteNonQueryAsync(ct);
    }

    internal async Task<bool> IsSpamAsync(MailMessageSummary message, CancellationToken ct = default)
    {
        // Cached message details may omit the RFC identity carried by their summary.
        if (string.IsNullOrEmpty(message.InternetMessageId))
            message = await LoadExactSpamCandidateAsync(message.AccountId, message.FolderName, message.MessageId, ct)
                ?? message;
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM SpamMessageState WHERE account_id=$a AND identity=$i)";
        command.Parameters.AddWithValue("$a", message.AccountId.ToString());
        command.Parameters.AddWithValue("$i", SpamIdentity(message));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0;
    }

    internal async Task<List<string>> SpamIdsAsync(Guid account, string folder, int? olderThanDays = null,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.unique_id FROM MessageSummary s
            WHERE s.account_id=$a AND s.folder_name=$f AND {SpamPredicate}
            """;
        command.Parameters.AddWithValue("$a", account.ToString());
        command.Parameters.AddWithValue("$f", folder);
        var ids = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        if (olderThanDays is not int days) return ids;
        // The caller must establish that this is the account's real Junk folder.
        // Start retention on observation in Junk, never from the message's original date.
        command.CommandText = $"""
            DELETE FROM SpamJunkAge WHERE account_id=$a AND folder_name=$f AND NOT EXISTS
              (SELECT 1 FROM MessageSummary s WHERE s.account_id=$a AND s.folder_name=$f
               AND s.unique_id=SpamJunkAge.unique_id AND {SpamPredicate});
            INSERT OR IGNORE INTO SpamJunkAge(account_id,folder_name,unique_id,first_seen_ticks)
              SELECT s.account_id,s.folder_name,s.unique_id,$now FROM MessageSummary s
              WHERE s.account_id=$a AND s.folder_name=$f AND {SpamPredicate};
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = """
            SELECT unique_id FROM SpamJunkAge WHERE account_id=$a AND folder_name=$f AND first_seen_ticks<=$cutoff
            """;
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-days).UtcTicks);
        var expired = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) expired.Add(reader.GetString(0));
        return ids.Where(expired.Contains).ToList();
    }
}
