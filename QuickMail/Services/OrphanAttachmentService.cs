using QuickMail.Models;
using System.IO;

namespace QuickMail.Services;

public sealed record OrphanAttachmentItem(string FullPath, DateTime LastWriteTime, long SizeBytes)
{
    public long SizeKb => (SizeBytes + 1023) / 1024;
}

public sealed class OrphanAttachmentService(LocalStoreService store, ProfileContext profile)
{
    public async Task<(IReadOnlyList<OrphanAttachmentItem> Items, IReadOnlyList<string> Roots)> ScanAsync(
        CancellationToken ct = default)
    {
        var candidates = await store.LoadAttachmentIndexCandidatesAsync(ct);
        var referenced = candidates
            .Select(item => Normalize(item.SourcePath))
            .Where(path => path is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = ResolveRoots(referenced.OfType<string>());
        var items = new List<OrphanAttachmentItem>();
        foreach (var root in roots)
        foreach (var path in EnumerateFilesWithoutLinks(root))
        {
            ct.ThrowIfCancellationRequested();
            var normalized = Normalize(path);
            if (normalized is null || referenced.Contains(normalized)) continue;
            try
            {
                var info = new FileInfo(normalized);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                    items.Add(new(normalized, info.LastWriteTime, info.Length));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (items, roots);
    }

    public void Remove(OrphanAttachmentItem item, IReadOnlyList<string> allowedRoots)
    {
        var path = Normalize(item.FullPath) ?? throw new IOException("The attachment path is invalid.");
        if (!allowedRoots.Any(root => IsWithin(path, root)))
            throw new IOException("The attachment is outside the folders scanned by this tool.");
        var info = new FileInfo(path);
        if (!info.Exists) return;
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Symbolic links and reparse points are not deleted.");
        if (info.Length != item.SizeBytes || info.LastWriteTime != item.LastWriteTime)
            throw new IOException("The file changed after the scan. Scan again before deleting it.");
        File.Delete(path);
    }

    private IReadOnlyList<string> ResolveRoots(IEnumerable<string> referencedPaths)
    {
        var metadata = EudoraImportMetadata.Load(profile.ProfileDir);
        if (metadata is not null && metadata.AttachmentMode.Equals("keep", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(metadata.SourceRoot))
        {
            try
            {
                return Directory.EnumerateDirectories(metadata.SourceRoot, "Attach*", SearchOption.TopDirectoryOnly)
                    .Where(path => !IsReparsePoint(path)).Select(Path.GetFullPath).ToList();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (metadata is null)
        {
            var inferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in referencedPaths)
            {
                for (var directory = Directory.GetParent(path); directory is not null; directory = directory.Parent)
                    if (directory.Name.Equals("Attach", StringComparison.OrdinalIgnoreCase)
                        || (directory.Name.StartsWith("Attach", StringComparison.OrdinalIgnoreCase)
                            && directory.Name[6..].All(char.IsDigit)))
                    { inferred.Add(directory.FullName); break; }
            }
            if (inferred.Count > 0) return inferred.Where(path => !IsReparsePoint(path)).ToList();
        }
        var local = Path.Combine(profile.ProfileDir, "Attachments");
        return Directory.Exists(local) && !IsReparsePoint(local) ? [Path.GetFullPath(local)] : [];
    }

    private static IEnumerable<string> EnumerateFilesWithoutLinks(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (IsReparsePoint(directory)) continue;
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory);
                children = Directory.GetDirectories(directory);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
            foreach (var child in children)
                if (!IsReparsePoint(child)) pending.Push(child);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    private static string? Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    private static bool IsWithin(string path, string root)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }
}
