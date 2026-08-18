using System.Text.Json;
using System.IO;
using Microsoft.Data.Sqlite;

namespace QuickMail.Services;

public sealed record AttachmentIndexCandidate(Guid AccountId, string MessageId, string FolderName,
    string FileName, string SourcePath, long DeclaredSize);
public sealed record ExtractedAttachmentText(string EntryPath, string Status, string Text);

public partial class LocalStoreService
{
    public async Task<IReadOnlyList<AttachmentIndexCandidate>> LoadAttachmentIndexCandidatesAsync(CancellationToken ct = default)
    {
        var result = new List<AttachmentIndexCandidate>();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id,unique_id,folder_name,attachments_json FROM MessageDetail WHERE attachments_json IS NOT NULL;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<IndexAttachmentMeta>>(reader.GetString(3));
                if (items is null) continue;
                foreach (var item in items)
                    if (!string.IsNullOrWhiteSpace(item.PartSpecifier) && Path.IsPathFullyQualified(item.PartSpecifier))
                        result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                            item.FileName, item.PartSpecifier, item.FileSize));
            }
            catch (JsonException) { }
        }
        return result;
    }

    public async Task<string?> GetAttachmentFingerprintAsync(AttachmentIndexCandidate item, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT fingerprint FROM AttachmentContent WHERE account_id=$a AND unique_id=$u AND folder_name=$f AND attachment_name=$n LIMIT 1;";
        cmd.Parameters.AddWithValue("$a", item.AccountId.ToString()); cmd.Parameters.AddWithValue("$u", item.MessageId);
        cmd.Parameters.AddWithValue("$f", item.FolderName); cmd.Parameters.AddWithValue("$n", item.FileName);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task ReplaceAttachmentIndexAsync(AttachmentIndexCandidate item, string fingerprint,
        IReadOnlyList<ExtractedAttachmentText> entries, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using (var deleteFts = conn.CreateCommand())
        {
            deleteFts.Transaction = tx;
            deleteFts.CommandText = "DELETE FROM AttachmentContentFts WHERE rowid IN (SELECT fts_rowid FROM AttachmentContent WHERE account_id=$a AND unique_id=$u AND folder_name=$f AND attachment_name=$n); DELETE FROM AttachmentContent WHERE account_id=$a AND unique_id=$u AND folder_name=$f AND attachment_name=$n;";
            AddAttachmentKey(deleteFts, item); await deleteFts.ExecuteNonQueryAsync(ct);
        }
        foreach (var entry in entries)
        {
            await using var fts = conn.CreateCommand(); fts.Transaction = tx;
            fts.CommandText = "INSERT INTO AttachmentContentFts(attachment_name,entry_path,content_text) VALUES($n,$e,$t);";
            fts.Parameters.AddWithValue("$n", item.FileName); fts.Parameters.AddWithValue("$e", entry.EntryPath);
            fts.Parameters.AddWithValue("$t", entry.Text); await fts.ExecuteNonQueryAsync(ct);
            await using var row = conn.CreateCommand(); row.Transaction = tx;
            row.CommandText = "INSERT INTO AttachmentContent(account_id,unique_id,folder_name,attachment_name,entry_path,source_path,fingerprint,status,content_text,fts_rowid) VALUES($a,$u,$f,$n,$e,$p,$fp,$s,$t,last_insert_rowid());";
            AddAttachmentKey(row, item); row.Parameters.AddWithValue("$e", entry.EntryPath);
            row.Parameters.AddWithValue("$p", item.SourcePath); row.Parameters.AddWithValue("$fp", fingerprint);
            row.Parameters.AddWithValue("$s", entry.Status); row.Parameters.AddWithValue("$t", entry.Text);
            await row.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static void AddAttachmentKey(SqliteCommand cmd, AttachmentIndexCandidate item)
    {
        cmd.Parameters.AddWithValue("$a", item.AccountId.ToString()); cmd.Parameters.AddWithValue("$u", item.MessageId);
        cmd.Parameters.AddWithValue("$f", item.FolderName); cmd.Parameters.AddWithValue("$n", item.FileName);
    }
    private sealed record IndexAttachmentMeta(string FileName, string ContentType, long FileSize, string? PartSpecifier);
}
