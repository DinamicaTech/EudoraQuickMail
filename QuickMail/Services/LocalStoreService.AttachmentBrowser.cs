using System.IO;
using System.Text.Json;
using QuickMail.Models;

namespace QuickMail.Services;

public partial class LocalStoreService
{
    public async Task<List<AttachmentBrowserItem>> BrowseAttachmentsAsync(string search,
        IReadOnlyList<MailMessageSummary>? scope, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync();
        if (scope != null)
        {
            await using var create = connection.CreateCommand();
            create.CommandText = "CREATE TEMP TABLE IF NOT EXISTS AttachmentScope(a TEXT,u TEXT,f TEXT,PRIMARY KEY(a,u,f)); DELETE FROM AttachmentScope;";
            await create.ExecuteNonQueryAsync(ct);
            using var transaction = connection.BeginTransaction();
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO AttachmentScope VALUES($a,$u,$f);";
            var a = insert.Parameters.Add("$a", Microsoft.Data.Sqlite.SqliteType.Text);
            var u = insert.Parameters.Add("$u", Microsoft.Data.Sqlite.SqliteType.Text);
            var f = insert.Parameters.Add("$f", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var message in scope)
            {
                ct.ThrowIfCancellationRequested();
                a.Value = message.AccountId.ToString(); u.Value = message.MessageId; f.Value = message.FolderName;
                await insert.ExecuteNonQueryAsync(ct);
            }
            transaction.Commit();
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.account_id,s.unique_id,s.folder_name,s.from_disp,s.subject,s.date_ticks,j.value
            FROM MessageDetail d JOIN MessageSummary s
              ON s.account_id=d.account_id AND s.unique_id=d.unique_id AND s.folder_name=d.folder_name
            JOIN json_each(CASE WHEN json_valid(d.attachments_json) THEN d.attachments_json ELSE '[]' END) j
            WHERE COALESCE(json_extract(j.value,'$.IsInline'),0)=0
              AND ($q='' OR instr(lower(COALESCE(json_extract(j.value,'$.FileName'),'')),lower($q))>0
                  OR instr(lower(s.subject),lower($q))>0 OR instr(lower(s.from_disp),lower($q))>0)
            """ + (scope == null ? "" : " AND EXISTS(SELECT 1 FROM AttachmentScope t WHERE t.a=s.account_id AND t.u=s.unique_id AND t.f=s.folder_name)") +
            " ORDER BY s.date_ticks DESC,s.account_id,s.unique_id,j.key LIMIT 10001;";
        command.Parameters.AddWithValue("$q", search.Trim());
        var results = new List<AttachmentBrowserItem>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var attachment = JsonSerializer.Deserialize<AttachmentModel>(reader.GetString(6));
            if (attachment == null) continue;
            results.Add(new(new MailMessageSummary
            {
                AccountId = Guid.Parse(reader.GetString(0)), MessageId = reader.GetString(1),
                FolderName = reader.GetString(2), From = reader.GetString(3), Subject = reader.GetString(4),
                Date = new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero)
            }, attachment));
        }
        return results;
    }

    public async Task<string?> GetRecoveryHashAsync(string path, CancellationToken ct)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256 FROM AttachmentFileHashCache WHERE source_path=$p;";
        command.Parameters.AddWithValue("$p", path);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task RelinkAttachmentAsync(AttachmentBrowserItem item, string replacement, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(replacement) || !File.Exists(replacement))
            throw new IOException("The replacement file is no longer available.");
        await using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT attachments_json FROM MessageDetail WHERE account_id=$a AND unique_id=$u AND folder_name=$f;";
        command.Parameters.AddWithValue("$a", item.Message.AccountId.ToString());
        command.Parameters.AddWithValue("$u", item.Message.MessageId);
        command.Parameters.AddWithValue("$f", item.Message.FolderName);
        var json = await command.ExecuteScalarAsync(ct) as string;
        var attachments = json == null ? null : JsonSerializer.Deserialize<List<AttachmentMeta>>(json);
        var indexes = attachments?.Select((a,i) => (a,i)).Where(x => x.a.FileName == item.Name && x.a.PartSpecifier == item.SourcePath).ToList();
        if (attachments == null || indexes?.Count != 1)
            throw new InvalidOperationException("The message or attachment has changed. Refresh the list before repairing it.");
        var index = indexes[0].i;
        attachments[index] = attachments[index] with { PartSpecifier = replacement, FileSize = new FileInfo(replacement).Length };
        command.CommandText = "UPDATE MessageDetail SET attachments_json=$json WHERE account_id=$a AND unique_id=$u AND folder_name=$f;";
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(attachments));
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "DELETE FROM AttachmentContentFts WHERE rowid IN (SELECT fts_rowid FROM AttachmentContent WHERE account_id=$a AND unique_id=$u AND folder_name=$f AND attachment_name=$n); DELETE FROM AttachmentContent WHERE account_id=$a AND unique_id=$u AND folder_name=$f AND attachment_name=$n;";
        command.Parameters.AddWithValue("$n", item.Name);
        await command.ExecuteNonQueryAsync(ct);
        transaction.Commit();
        await RecordMessageActivityAsync(item.Message, "Attachment repaired", item.Name, ct);
    }

    public string RecoveredAttachmentDirectory => Path.Combine(Path.GetDirectoryName(_dbPath)!, "Attachments", "Recovered");
}
