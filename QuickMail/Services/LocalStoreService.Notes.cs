using Microsoft.Data.Sqlite;

namespace QuickMail.Services;

public partial class LocalStoreService
{
    // A separate, initially empty FTS avoids rebuilding the large body index on upgrade.
    private static void InitializeNotes(SqliteConnection conn)
    {
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN notes TEXT NOT NULL DEFAULT '';");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_summary_account_internet_id ON MessageSummary(account_id,internet_message_id);
            CREATE TABLE IF NOT EXISTS PrivateMessageNotes(account_id TEXT NOT NULL, internet_message_id TEXT NOT NULL, notes TEXT NOT NULL,
                PRIMARY KEY(account_id,internet_message_id));
            CREATE VIRTUAL TABLE IF NOT EXISTS MessageNotesFts USING fts5(notes,account_id UNINDEXED,unique_id UNINDEXED,folder_name UNINDEXED);
            CREATE TRIGGER IF NOT EXISTS notes_identity_insert AFTER INSERT ON MessageSummary WHEN new.internet_message_id<>'' BEGIN
                INSERT OR IGNORE INTO PrivateMessageNotes(account_id,internet_message_id,notes) SELECT new.account_id,new.internet_message_id,new.notes WHERE new.notes<>'';
                UPDATE MessageSummary SET notes=COALESCE((SELECT notes FROM PrivateMessageNotes WHERE account_id=new.account_id AND internet_message_id=new.internet_message_id),new.notes) WHERE rowid=new.rowid;
            END;
            CREATE TRIGGER IF NOT EXISTS notes_identity_update AFTER UPDATE OF internet_message_id ON MessageSummary WHEN new.internet_message_id<>'' AND new.internet_message_id<>old.internet_message_id BEGIN
                UPDATE MessageSummary SET notes=COALESCE((SELECT notes FROM PrivateMessageNotes WHERE account_id=new.account_id AND internet_message_id=new.internet_message_id),new.notes) WHERE rowid=new.rowid;
            END;
            CREATE TRIGGER IF NOT EXISTS notes_remember AFTER UPDATE OF notes ON MessageSummary WHEN new.internet_message_id<>'' AND new.notes<>old.notes BEGIN
                INSERT INTO PrivateMessageNotes(account_id,internet_message_id,notes) VALUES(new.account_id,new.internet_message_id,new.notes)
                    ON CONFLICT(account_id,internet_message_id) DO UPDATE SET notes=excluded.notes;
                UPDATE MessageSummary SET notes=new.notes WHERE account_id=new.account_id AND internet_message_id=new.internet_message_id AND notes<>new.notes;
            END;
            CREATE TRIGGER IF NOT EXISTS notes_insert AFTER INSERT ON MessageSummary WHEN new.notes<>'' BEGIN
                DELETE FROM MessageNotesFts WHERE account_id=new.account_id AND unique_id=new.unique_id AND folder_name=new.folder_name;
                INSERT INTO MessageNotesFts(notes,account_id,unique_id,folder_name)
                    SELECT notes,account_id,unique_id,folder_name FROM MessageSummary WHERE rowid=new.rowid AND notes<>'';
            END;
            CREATE TRIGGER IF NOT EXISTS notes_update AFTER UPDATE OF notes ON MessageSummary WHEN new.notes<>old.notes BEGIN
                DELETE FROM MessageNotesFts WHERE account_id=old.account_id AND unique_id=old.unique_id AND folder_name=old.folder_name;
                INSERT INTO MessageNotesFts(notes,account_id,unique_id,folder_name)
                    SELECT new.notes,new.account_id,new.unique_id,new.folder_name WHERE new.notes<>'';
            END;
            CREATE TRIGGER IF NOT EXISTS notes_move AFTER UPDATE OF folder_name,unique_id ON MessageSummary WHEN old.notes<>'' BEGIN
                UPDATE MessageNotesFts SET account_id=new.account_id,unique_id=new.unique_id,folder_name=new.folder_name
                    WHERE account_id=old.account_id AND unique_id=old.unique_id AND folder_name=old.folder_name;
            END;
            CREATE TRIGGER IF NOT EXISTS notes_delete AFTER DELETE ON MessageSummary WHEN old.notes<>'' BEGIN
                DELETE FROM MessageNotesFts WHERE account_id=old.account_id AND unique_id=old.unique_id AND folder_name=old.folder_name;
            END;
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task SavePrivateNotesAsync(Guid accountId, string? internetMessageId, string notes)
    {
        if (string.IsNullOrWhiteSpace(internetMessageId)) return;
        await using var conn = await OpenAsync();
        if (string.IsNullOrEmpty(notes))
        {
            await using var exists = conn.CreateCommand();
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM PrivateMessageNotes WHERE account_id=$a AND internet_message_id=$id);";
            exists.Parameters.AddWithValue("$a", accountId.ToString());
            exists.Parameters.AddWithValue("$id", internetMessageId);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync()) == 0) return;
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PrivateMessageNotes(account_id,internet_message_id,notes)
                SELECT $a,$id,$notes WHERE $notes<>'' OR EXISTS(SELECT 1 FROM PrivateMessageNotes WHERE account_id=$a AND internet_message_id=$id)
                ON CONFLICT(account_id,internet_message_id) DO UPDATE SET notes=excluded.notes;
            UPDATE MessageSummary SET notes=$notes WHERE account_id=$a AND internet_message_id=$id AND notes<>$notes;
            """;
        cmd.Parameters.AddWithValue("$a", accountId.ToString());
        cmd.Parameters.AddWithValue("$id", internetMessageId);
        cmd.Parameters.AddWithValue("$notes", notes);
        await cmd.ExecuteNonQueryAsync();
    }
}
