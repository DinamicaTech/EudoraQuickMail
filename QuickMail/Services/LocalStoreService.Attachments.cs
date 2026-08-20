using System.Text.Json;
using System.IO;
using Microsoft.Data.Sqlite;

namespace QuickMail.Services;

public sealed record AttachmentIndexCandidate(Guid AccountId, string MessageId, string FolderName,
    string FileName, string SourcePath, long DeclaredSize);
public sealed record ExtractedAttachmentText(string EntryPath, string Status, string Text);

public partial class LocalStoreService
{
    public async Task<bool> NeedsInitialAttachmentIndexAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(SELECT 1 FROM MessageSummary WHERE has_attachments=1 LIMIT 1)
               AND NOT EXISTS(SELECT 1 FROM AttachmentContent LIMIT 1);
            """;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0) != 0;
    }

    public async Task<int> RebuildMessageSearchIndexAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM LocalMessageFts;
            DELETE FROM LocalMessageFtsKey;
            INSERT INTO LocalMessageFts(account_id,unique_id,folder_name,from_addr,to_addr,cc_addr,subject,body_text)
            SELECT s.account_id,s.unique_id,s.folder_name,s.from_disp,d.to_addr,d.cc,s.subject,
                   CASE WHEN trim(COALESCE(d.plain_body,'')) <> '' THEN d.plain_body ELSE COALESCE(d.html_body,'') END
              FROM MessageSummary s
              LEFT JOIN MessageDetail d ON d.account_id=s.account_id AND d.unique_id=s.unique_id AND d.folder_name=s.folder_name;
            INSERT INTO LocalMessageFtsKey(account_id,unique_id,folder_name,fts_rowid)
            SELECT account_id,unique_id,folder_name,rowid FROM LocalMessageFts;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = "SELECT count(*) FROM LocalMessageFtsKey;";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        await tx.CommitAsync(ct);
        return count;
    }

    public async Task ResetAttachmentSearchIndexAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
        // Keep both SHA caches: rebuilding message associations must not re-extract unchanged files.
        cmd.CommandText = "DELETE FROM AttachmentContentFts; DELETE FROM AttachmentContent;";
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

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

    public async Task<string?> GetCachedAttachmentHashAsync(string sourcePath, long length, long lastWriteUtcTicks,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sha256 FROM AttachmentFileHashCache WHERE source_path=$p AND file_length=$l AND last_write_utc_ticks=$t;";
        cmd.Parameters.AddWithValue("$p", sourcePath); cmd.Parameters.AddWithValue("$l", length);
        cmd.Parameters.AddWithValue("$t", lastWriteUtcTicks);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SaveAttachmentHashAsync(string sourcePath, long length, long lastWriteUtcTicks, string sha256,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO AttachmentFileHashCache(source_path,file_length,last_write_utc_ticks,sha256) VALUES($p,$l,$t,$h) ON CONFLICT(source_path) DO UPDATE SET file_length=excluded.file_length,last_write_utc_ticks=excluded.last_write_utc_ticks,sha256=excluded.sha256;";
        cmd.Parameters.AddWithValue("$p", sourcePath); cmd.Parameters.AddWithValue("$l", length);
        cmd.Parameters.AddWithValue("$t", lastWriteUtcTicks); cmd.Parameters.AddWithValue("$h", sha256);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ExtractedAttachmentText>?> LoadCachedExtractionAsync(string sha256,
        string extractionKey, CancellationToken ct = default)
    {
        var result = new List<ExtractedAttachmentText>();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entry_path,status,content_text FROM AttachmentExtractionCache WHERE sha256=$h AND extraction_key=$k ORDER BY entry_path;";
        cmd.Parameters.AddWithValue("$h", sha256); cmd.Parameters.AddWithValue("$k", extractionKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result.Count == 0 ? null : result;
    }

    public async Task<IReadOnlyList<ExtractedAttachmentText>?> LoadExistingAttachmentExtractionAsync(
        AttachmentIndexCandidate item, CancellationToken ct = default)
    {
        var result = new List<ExtractedAttachmentText>();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entry_path,status,content_text FROM AttachmentContent WHERE account_id=$a AND unique_id=$u AND folder_name=$f AND attachment_name=$n ORDER BY entry_path;";
        AddAttachmentKey(cmd, item);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result.Count == 0 ? null : result;
    }

    public async Task SaveCachedExtractionAsync(string sha256, string extractionKey,
        IReadOnlyList<ExtractedAttachmentText> entries, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        foreach (var entry in entries)
        {
            await using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO AttachmentExtractionCache(sha256,extraction_key,entry_path,status,content_text) VALUES($h,$k,$e,$s,$t);";
            cmd.Parameters.AddWithValue("$h", sha256); cmd.Parameters.AddWithValue("$k", extractionKey);
            cmd.Parameters.AddWithValue("$e", entry.EntryPath); cmd.Parameters.AddWithValue("$s", entry.Status);
            cmd.Parameters.AddWithValue("$t", entry.Text); await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
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
