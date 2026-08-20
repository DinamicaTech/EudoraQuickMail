using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EudoraImporter;

internal static class AttachmentRelocator
{
    internal sealed record CopiedFile(string Source, string Destination, long Length);
    internal sealed record Result(IReadOnlyList<CopiedFile> Files)
    {
        public static readonly Result Empty = new([]);
    }

    private sealed class AttachmentData
    {
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = "application/octet-stream";
        public long FileSize { get; set; }
        public string? PartSpecifier { get; set; }
    }

    public static async Task<Result> CopyReferencedAsync(string database, string eudoraRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        var copied = new Dictionary<string, CopiedFile>(StringComparer.OrdinalIgnoreCase);
        var updates = new List<(long Id, string Json)>();
        await using var connection = new SqliteConnection($"Data Source={database};Mode=ReadWrite;Pooling=False;");
        await connection.OpenAsync();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT id,attachments_json FROM messages WHERE attachments_json IS NOT NULL;";
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var attachments = JsonSerializer.Deserialize<List<AttachmentData>>(reader.GetString(1)) ?? [];
                var changed = false;
                foreach (var attachment in attachments)
                {
                    var source = attachment.PartSpecifier;
                    if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source) || !File.Exists(source)) continue;
                    source = Path.GetFullPath(source);
                    if (!copied.TryGetValue(source, out var copy))
                    {
                        var info = new FileInfo(source);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                            throw new IOException($"Refusing to copy linked attachment: {source}");
                        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
                        var safeName = Path.GetFileName(source);
                        var destination = Path.Combine(destinationRoot, hash + "-" + safeName);
                        File.Copy(source, destination, overwrite: true);
                        var destinationLength = new FileInfo(destination).Length;
                        if (destinationLength != info.Length)
                            throw new IOException($"Attachment verification failed: {source}");
                        copy = new CopiedFile(source, destination, info.Length);
                        copied.Add(source, copy);
                        Console.WriteLine($"Attachment copied: {safeName}");
                    }
                    attachment.PartSpecifier = copy.Destination;
                    attachment.FileSize = copy.Length;
                    changed = true;
                }
                if (changed) updates.Add((reader.GetInt64(0), JsonSerializer.Serialize(attachments)));
            }
        }
        await using var tx = await connection.BeginTransactionAsync();
        foreach (var update in updates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)tx;
            command.CommandText = "UPDATE messages SET attachments_json=$json WHERE id=$id;";
            command.Parameters.AddWithValue("$json", update.Json);
            command.Parameters.AddWithValue("$id", update.Id);
            await command.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        Console.WriteLine($"Referenced attachments copied: {copied.Count:N0}");
        return new Result(copied.Values.ToList());
    }

    public static void DeleteVerifiedSources(Result result, string eudoraRoot)
    {
        var root = Path.GetFullPath(eudoraRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var removed = 0;
        foreach (var item in result.Files)
        {
            var source = Path.GetFullPath(item.Source);
            if (!source.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Refusing to remove an attachment outside the selected Eudora folder: {source}");
            var sourceInfo = new FileInfo(source);
            var destinationInfo = new FileInfo(item.Destination);
            if (!sourceInfo.Exists || !destinationInfo.Exists || sourceInfo.Length != item.Length || destinationInfo.Length != item.Length)
                throw new IOException($"Attachment changed during import; original was kept: {source}");
            if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Refusing to remove a linked attachment: {source}");
            File.Delete(source);
            removed++;
        }
        Console.WriteLine($"Referenced attachments removed from Eudora: {removed:N0}");
    }
}
