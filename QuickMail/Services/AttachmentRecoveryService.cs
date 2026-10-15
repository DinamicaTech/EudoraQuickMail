using System.IO;
using System.Security.Cryptography;
using QuickMail.Models;

namespace QuickMail.Services;

public static class AttachmentRecoveryService
{
    public static async Task<List<AttachmentRecoveryCandidate>> FindAsync(AttachmentBrowserItem item,
        string root, string? expectedHash, IProgress<string>? progress, CancellationToken ct)
    {
        root = Path.GetFullPath(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Choose a real directory, not a junction or symbolic link.");
        var found = new List<AttachmentRecoveryCandidate>();
        var pending = new Stack<string>(); pending.Push(root);
        var visited = 0; var skipped = 0;
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is DirectoryInfo sub) { pending.Push(sub.FullName); continue; }
                    if (entry is not FileInfo file) continue;
                    if (++visited % 100 == 0) progress?.Report($"Checked {visited:N0} files; {found.Count:N0} candidates; skipped {skipped:N0} directories.");
                    if (!string.Equals(file.Name, item.Name, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(file.Name, Path.GetFileName(item.SourcePath), StringComparison.OrdinalIgnoreCase)) continue;
                    var evidence = item.Size > 0 && file.Length == item.Size ? "Name and size match (content unverified)" : "Name matches; size differs or is unknown";
                    if (!string.IsNullOrEmpty(expectedHash))
                    {
                        await using var stream = file.OpenRead();
                        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                        evidence = string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase)
                            ? "SHA-256 verified" : "SHA-256 mismatch — not the original file";
                    }
                    found.Add(new(file.FullName, file.Length, evidence));
                    if (found.Count >= 1000) throw new InvalidOperationException("More than 1,000 candidates. Choose a narrower search directory.");
                }
            }
            catch (UnauthorizedAccessException) { skipped++; }
            catch (IOException) { skipped++; }
        }
        progress?.Report($"Checked {visited:N0} files; {found.Count:N0} candidates; {skipped:N0} inaccessible locations skipped.");
        return found;
    }

    public static async Task VerifyAsync(string path, string? expectedHash, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The selected file no longer exists.", path);
        if (string.IsNullOrEmpty(expectedHash)) return;
        await using var stream = File.OpenRead(path);
        if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected file does not match the recorded SHA-256. The attachment was not changed.");
    }

    public static async Task<string> CopyUniqueAsync(string source, string directory, string name, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        name = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(name)) name = "attachment";
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        await using var input = File.OpenRead(source);
        for (var ordinal = 1; ; ordinal++)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(directory, ordinal == 1 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({ordinal}){Path.GetExtension(name)}");
            FileStream output;
            try { output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(target)) { continue; }
            try { await using (output) await input.CopyToAsync(output, ct); }
            catch
            {
                // Only this newly-created output belongs to the failed operation; never touch the source.
                File.Delete(target);
                throw;
            }
            return target;
        }
    }
}
