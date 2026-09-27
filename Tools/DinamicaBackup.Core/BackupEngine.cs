using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DinamicaBackup;

/// <summary>Call only while the coordinator owns the exclusive profile lease.</summary>
public sealed class BackupEngine(BackupJob job, IProgress<ProgressInfo>? progress = null)
{
    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
    private static async Task<(long Length, string Hash)> CopyHashed(Stream source, Stream target, CancellationToken ct, Action<long>? report = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024]; long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            hash.AppendData(buffer, 0, read); await target.WriteAsync(buffer.AsMemory(0, read), ct);
            length += read; report?.Invoke(length);
        }
        return (length, Convert.ToHexString(hash.GetHashAndReset()));
    }
    public async Task<Manifest> BackupAsync(RunResult run, CancellationToken ct)
    {
        SafePaths.Validate(job);
        var database = SafePaths.Child(job.ProfileDirectory, job.DatabaseName);
        SafePaths.CheckSpace(job.Destination, new FileInfo(database).Length);
        Directory.CreateDirectory(job.Destination);
        SqliteValidation.Checkpoint(database, progress);
        var inspection = SqliteValidation.Inspect(database, job.Probes, false, ct, progress);
        var manifest = new Manifest { JobId = job.Id, DatabaseName = job.DatabaseName, SourceProfile = job.ProfileDirectory, JobConfiguration = job, SchemaVersion = inspection.Version, ProbeValues = inspection.Probes, Probes = job.Probes.ToList(), IncludesAttachments = job.CopyAttachments };
        var final = SafePaths.Child(job.Destination, $"backup-{manifest.CreatedUtc:yyyyMMdd-HHmmss}-{manifest.BackupId}.zip");
        var temporary = final + ".partial";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var relative in job.ProfileFiles.Prepend(job.DatabaseName).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    var path = SafePaths.Child(job.ProfileDirectory, relative);
                    if (!File.Exists(path))
                    {
                        if (relative == job.DatabaseName) throw new FileNotFoundException("Database not found.", path);
                        continue;
                    }
                    var info = new FileInfo(path);
                    var entry = zip.CreateEntry("profile/" + relative.Replace('\\', '/'), CompressionLevel.Optimal);
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                    await using var output = entry.Open();
                    var result = await CopyHashed(input, output, ct, done => progress?.Report(new("Compressing " + relative, done, info.Length)));
                    manifest.Files.Add(new(relative, result.Length, info.LastWriteTimeUtc, result.Hash)); run.FilesCopied++;
                }
                if (job.CopyAttachments) manifest.Attachments = await MirrorAttachments(run, ct);
                var metadata = zip.CreateEntry("manifest.json");
                await using var manifestStream = metadata.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonFiles.Options, ct);
            }
            progress?.Report(new("Verifying ZIP contents"));
            await VerifyArchiveAsync(temporary, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, final);
            run.Artifact = final;
            JsonFiles.Write(final + ".manifest.json", manifest);
            ApplyRetention();
            return manifest;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<List<FileRecord>> MirrorAttachments(RunResult run, CancellationToken ct)
    {
        Directory.CreateDirectory(job.AttachmentDestination);
        var marker = SafePaths.Child(job.AttachmentDestination, ".backup-owner");
        if (File.Exists(marker))
        {
            if (File.ReadAllText(marker) != job.Id) throw new IOException("Attachment destination belongs to a different job.");
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(job.AttachmentDestination).Any()) throw new IOException("Choose an empty attachment destination for the first backup.");
            File.WriteAllText(marker, job.Id);
        }
        var indexPath = SafePaths.Child(job.AttachmentDestination, ".attachment-manifest.json");
        var old = File.Exists(indexPath) ? JsonFiles.Read<List<FileRecord>>(indexPath) : [];
        var oldMap = old.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var result = new List<FileRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingRoots = new List<string>();
        foreach (var relativeRoot in job.AttachmentDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var root = SafePaths.Child(job.ProfileDirectory, relativeRoot);
            if (!Directory.Exists(root)) { missingRoots.Add(relativeRoot); continue; }
            foreach (var file in SafePaths.Files(root))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(job.ProfileDirectory, file).Replace('\\', '/');
                if (!seen.Add(relative)) continue;
                var target = SafePaths.Child(job.AttachmentDestination, relative);
                var info = new FileInfo(file); var targetInfo = new FileInfo(target);
                if (oldMap.TryGetValue(relative, out var prior) && prior.Length == info.Length && prior.ModifiedUtc == info.LastWriteTimeUtc && targetInfo.Exists && targetInfo.Length == prior.Length && targetInfo.LastWriteTimeUtc == prior.ModifiedUtc)
                { result.Add(prior); continue; }
                progress?.Report(new("Copying attachment " + relative));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var part = target + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    (long Length, string Hash) copy;
                    await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
                    await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                        copy = await CopyHashed(input, output, ct);
                    if (await HashAsync(part, ct) != copy.Hash) throw new IOException("Attachment verification failed: " + relative);
                    File.Move(part, target, true); File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);
                    result.Add(new(relative, copy.Length, info.LastWriteTimeUtc, copy.Hash)); run.FilesCopied++;
                }
                finally { if (File.Exists(part)) File.Delete(part); }
            }
        }
        var names = result.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = old.Where(x => !names.Contains(x.Path)).ToArray();
        if (!job.DeleteMissingAttachments && removed.Length > 0 &&
            (missingRoots.Count > 0 || removed.Length * 100.0 / Math.Max(1, old.Count) > job.MaximumDeletionPercent))
            run.Warnings.Add("Source attachment inventory dropped significantly; destination files have been preserved.");
        if (job.DeleteMissingAttachments && removed.Length > 0)
        {
            if (missingRoots.Count > 0 || removed.Length * 100.0 / Math.Max(1, old.Count) > job.MaximumDeletionPercent)
            {
                run.Warnings.Add("Attachment removals suspended: missing source directory or deletion threshold exceeded.");
                result.AddRange(removed); // Retain inventory so a later run can retry safely.
            }
            else
            {
                var targets = removed.Select(x => SafePaths.Child(job.AttachmentDestination, x.Path)).ToArray();
                foreach (var target in targets) { ct.ThrowIfCancellationRequested(); if (File.Exists(target)) File.Delete(target); }
            }
        }
        else result.AddRange(removed);
        JsonFiles.Write(indexPath, result);
        return result;
    }
    public static Manifest ReadManifest(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Backup has no manifest.");
        if (entry.Length > 256L * 1024 * 1024) throw new InvalidDataException("Manifest is too large.");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<Manifest>(stream, JsonFiles.Options) ?? throw new InvalidDataException("Invalid manifest.");
        if (manifest.FormatVersion != 1 || !Guid.TryParse(manifest.JobId, out _) || !Guid.TryParse(manifest.BackupId, out _)) throw new InvalidDataException("Unsupported backup manifest.");
        if (manifest.Files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count || !manifest.Files.Any(x => x.Path == manifest.DatabaseName)) throw new InvalidDataException("Invalid backup file list.");
        return manifest;
    }
    public static async Task VerifyArchiveAsync(string archive, CancellationToken ct)
    {
        var manifest = ReadManifest(archive);
        using var zip = ZipFile.OpenRead(archive);
        foreach (var record in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var name = "profile/" + record.Path.Replace('\\', '/');
            if (zip.Entries.Count(x => x.FullName == name) != 1) throw new InvalidDataException("Missing/duplicate archive member: " + name);
            var entry = zip.GetEntry(name)!;
            if (entry.Length != record.Length) throw new InvalidDataException("Archive length mismatch: " + name);
            await using var stream = entry.Open();
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (actual != record.Sha256) throw new InvalidDataException("Archive hash mismatch: " + name);
        }
    }
    public IEnumerable<(string Path, Manifest Manifest)> Backups()
    {
        if (!Directory.Exists(job.Destination)) yield break;
        foreach (var path in Directory.EnumerateFiles(job.Destination, "backup-*.zip"))
        {
            var manifest = ReadManifest(path);
            if (manifest.JobId == job.Id) yield return (path, manifest);
        }
    }
    public void ApplyRetention()
    {
        var all = Backups().OrderByDescending(x => x.Manifest.CreatedUtc).ToArray();
        var keep = all.Take(1).Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in all.GroupBy(x => x.Manifest.CreatedUtc.LocalDateTime.Date).Take(job.KeepDaily).Select(x => x.First())) keep.Add(item.Path);
        foreach (var item in all.GroupBy(x => { var d = x.Manifest.CreatedUtc.LocalDateTime.Date; return d.AddDays(-((int)d.DayOfWeek + 6) % 7); }).Take(job.KeepWeekly).Select(x => x.First())) keep.Add(item.Path);
        foreach (var item in all.GroupBy(x => x.Manifest.CreatedUtc.LocalDateTime.ToString("yyyy-MM")).Take(job.KeepMonthly).Select(x => x.First())) keep.Add(item.Path);
        var targets = all.Where(x => !keep.Contains(x.Path)).Select(x => x.Path).ToArray();
        foreach (var path in targets) { SafePaths.NoLinks(path); if (!SafePaths.Within(job.Destination, path)) throw new IOException("Invalid retention target."); }
        foreach (var path in targets) { File.Delete(path); if (File.Exists(path + ".manifest.json")) File.Delete(path + ".manifest.json"); }
    }
}
