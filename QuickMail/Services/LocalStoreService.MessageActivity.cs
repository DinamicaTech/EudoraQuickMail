using Microsoft.Data.Sqlite;
using QuickMail.Models;

namespace QuickMail.Services;

public sealed record MessageActivityEntry(DateTimeOffset When, string Action, string Description);

public partial class LocalStoreService
{
    // Kept separately from message content: no body, recipient lists, credentials or attachments.
    // Triggers describe LOCAL observations, not authoritative server arrival/deletion times.
    private static void InitializeMessageActivity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS MessageActivity(
                id INTEGER PRIMARY KEY, account_id TEXT NOT NULL, message_key TEXT NOT NULL,
                recorded INTEGER NOT NULL DEFAULT(unixepoch()), action TEXT NOT NULL, description TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_activity_message ON MessageActivity(account_id,message_key,id);
            CREATE INDEX IF NOT EXISTS idx_activity_time ON MessageActivity(recorded);
            CREATE TRIGGER IF NOT EXISTS qm_activity_insert AFTER INSERT ON MessageSummary BEGIN
              INSERT INTO MessageActivity(account_id,message_key,action,description)
                VALUES(NEW.account_id, CASE WHEN trim(NEW.internet_message_id)<>'' THEN 'mid:'||trim(NEW.internet_message_id,'<> ')
                  ELSE 'local:'||NEW.folder_name||char(31)||NEW.unique_id END,
                  'Observed locally','Added to local catalogue: '||NEW.folder_name);
            END;
            CREATE TRIGGER IF NOT EXISTS qm_activity_move AFTER UPDATE OF folder_name,unique_id,internet_message_id ON MessageSummary
              WHEN OLD.folder_name<>NEW.folder_name OR OLD.unique_id<>NEW.unique_id OR OLD.internet_message_id<>NEW.internet_message_id BEGIN
              UPDATE MessageActivity SET message_key=CASE WHEN trim(NEW.internet_message_id)<>'' THEN 'mid:'||trim(NEW.internet_message_id,'<> ')
                  ELSE 'local:'||NEW.folder_name||char(31)||NEW.unique_id END
                WHERE account_id=OLD.account_id AND message_key=CASE WHEN trim(OLD.internet_message_id)<>'' THEN 'mid:'||trim(OLD.internet_message_id,'<> ')
                  ELSE 'local:'||OLD.folder_name||char(31)||OLD.unique_id END;
              INSERT INTO MessageActivity(account_id,message_key,action,description)
                SELECT NEW.account_id, CASE WHEN trim(NEW.internet_message_id)<>'' THEN 'mid:'||trim(NEW.internet_message_id,'<> ')
                  ELSE 'local:'||NEW.folder_name||char(31)||NEW.unique_id END,
                  'Moved',OLD.folder_name||' → '||NEW.folder_name WHERE OLD.folder_name<>NEW.folder_name;
            END;
            CREATE TRIGGER IF NOT EXISTS qm_activity_delete AFTER DELETE ON MessageSummary BEGIN
              INSERT INTO MessageActivity(account_id,message_key,action,description)
                VALUES(OLD.account_id,CASE WHEN trim(OLD.internet_message_id)<>'' THEN 'mid:'||trim(OLD.internet_message_id,'<> ')
                  ELSE 'local:'||OLD.folder_name||char(31)||OLD.unique_id END,
                  'Removed locally','Removed from local catalogue: '||OLD.folder_name);
            END;
            CREATE TRIGGER IF NOT EXISTS qm_activity_bound AFTER INSERT ON MessageActivity BEGIN
              DELETE FROM MessageActivity WHERE id <= NEW.id-100000;
            END;
            DELETE FROM MessageActivity WHERE recorded < unixepoch()-7776000;
            """;
        command.ExecuteNonQuery();
    }

    private static string ActivityKey(MailMessageSummary message) => string.IsNullOrWhiteSpace(message.InternetMessageId)
        ? "local:" + message.FolderName + '\u001f' + message.MessageId
        : "mid:" + message.InternetMessageId.Trim('<', '>', ' ');

    private static async Task<string> ResolveActivityKeyAsync(SqliteConnection connection, MailMessageSummary message, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(message.InternetMessageId)) return ActivityKey(message);
        await using var lookup = connection.CreateCommand();
        lookup.CommandText = "SELECT internet_message_id FROM MessageSummary WHERE account_id=$account AND folder_name=$folder AND unique_id=$id;";
        lookup.Parameters.AddWithValue("$account", message.AccountId.ToString());
        lookup.Parameters.AddWithValue("$folder", message.FolderName);
        lookup.Parameters.AddWithValue("$id", message.MessageId);
        return await lookup.ExecuteScalarAsync(ct) is string internet && !string.IsNullOrWhiteSpace(internet)
            ? "mid:" + internet.Trim('<', '>', ' ') : ActivityKey(message);
    }

    public async Task RecordMessageActivityAsync(MailMessageSummary message, string action, string description, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await OpenAsync();
            var key = await ResolveActivityKeyAsync(connection, message, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO MessageActivity(account_id,message_key,action,description) VALUES($account,$key,$action,$description);";
            command.Parameters.AddWithValue("$account", message.AccountId.ToString());
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$action", action);
            command.Parameters.AddWithValue("$description", description.Length > 2000 ? description[..2000] : description);
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) { LogService.Log("Could not record message activity", ex); }
    }

    public async Task<IReadOnlyList<MessageActivityEntry>> LoadMessageActivityAsync(MailMessageSummary message, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT recorded,action,description FROM MessageActivity WHERE account_id=$account
              AND message_key=$key AND recorded>=unixepoch()-7776000 ORDER BY id DESC LIMIT 500;
            """;
        command.Parameters.AddWithValue("$account", message.AccountId.ToString());
        command.Parameters.AddWithValue("$key", await ResolveActivityKeyAsync(connection, message, ct));
        var entries = new List<MessageActivityEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) entries.Add(new(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2)));
        return entries;
    }
}
