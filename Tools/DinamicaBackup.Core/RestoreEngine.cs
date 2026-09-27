using System.IO.Compression;
using System.Security.Cryptography;

namespace DinamicaBackup;

public enum RestoreMode { Database, DatabaseAndAttachments, Attachments, SelectedAttachments }

public sealed class RestoreEngine(BackupJob job, IProgress<ProgressInfo>? progress = null)
{
    public async Task<Manifest> ExtractAsync(string archive, string target, CancellationToken ct)
    {
        var manifest = BackupEngine.ReadManifest(archive);
        if (manifest.JobId != job.Id) throw new InvalidDataException("This backup belongs to a different job.");
        ValidateFileList(manifest);
        SafePaths.NoLinks(target);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("Extraction requires an empty directory.");
        SafePaths.CheckSpace(target, manifest.Files.Sum(x => x.Length));
        Directory.CreateDirectory(target);
        File.WriteAllText(SafePaths.Child(target, ".backup-owner"), job.Id);
        using var zip = ZipFile.OpenRead(archive);
        foreach (var record in manifest.Files)
        {
            ct.ThrowIfCancellationRequested(); progress?.Report(new("Restoring " + record.Path));
            var name = "profile/" + record.Path.Replace('\\', '/');
            if (zip.Entries.Count(e => e.FullName == name) != 1) throw new InvalidDataException("Missing/duplicate ZIP entry.");
            var entry = zip.GetEntry(name)!;
            if (entry.Length != record.Length) throw new InvalidDataException("Unexpected ZIP entry size.");
            var path = SafePaths.Child(target, record.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var source = entry.Open())
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                await source.CopyToAsync(output, ct);
            if (await BackupEngine.HashAsync(path, ct) != record.Sha256) throw new InvalidDataException("Restored hash mismatch: " + record.Path);
        }
        progress?.Report(new("Checking restored SQLite database"));
        var check = SqliteValidation.Inspect(SafePaths.Child(target, manifest.DatabaseName), manifest.Probes, job.FullIntegrityCheck, ct, progress);
        if (check.Version != manifest.SchemaVersion || check.Probes.Count != manifest.ProbeValues.Count || check.Probes.Any(x => !manifest.ProbeValues.TryGetValue(x.Key, out var value) || value != x.Value))
            throw new InvalidDataException("Restored database does not match captured validation values.");
        return manifest;
    }
    public async Task TestAsync(string archive, Catalog catalog, RunResult run, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.RestoreTestDirectory)) throw new InvalidOperationException("Choose a restore-test directory first.");
        SafePaths.Validate(job);
        var directory = SafePaths.Child(job.RestoreTestDirectory, "test-" + run.Id);
        run.Artifact = directory;
        var manifest = await ExtractAsync(archive, directory, ct);
        run.FilesCopied = manifest.Files.Count;
        if (manifest.IncludesAttachments && job.AttachmentSampleSize > 0)
        {
            if (string.IsNullOrWhiteSpace(job.AttachmentDestination)) throw new IOException("Attachment repository is not configured.");
            var verified = catalog.Verifications();
            var random = Random.Shared;
            var inventory = manifest.Attachments;
            progress?.Report(new("Checking attachment directory structure"));
            var missingDirectories = new List<string>();
            foreach (var attachmentDirectory in inventory.Select(x => Path.GetDirectoryName(x.Path)).OfType<string>().Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(SafePaths.Child(job.AttachmentDestination, attachmentDirectory))) missingDirectories.Add(attachmentDirectory);
            }
            if (missingDirectories.Count > 0)
                run.Warnings.Add($"Missing attachment directories: {missingDirectories.Count}. " + string.Join(", ", missingDirectories.Take(10)));
            // Half stratified random, half least-recently checked. Never sample actual surviving files.
            var first = inventory.GroupBy(x => Path.GetDirectoryName(x.Path) ?? "")
                .OrderBy(_ => random.Next()).Select(g => g.ElementAt(random.Next(g.Count())))
                .Take((job.AttachmentSampleSize + 1) / 2);
            var sample = first.Concat(inventory.OrderBy(x => verified.GetValueOrDefault(x.Path, "")).ThenBy(_ => random.Next()))
                .DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(job.AttachmentSampleSize).ToArray();
            var completed = 0;
            foreach (var item in sample)
            {
                ct.ThrowIfCancellationRequested(); progress?.Report(new("Checking attachment " + item.Path, completed++, sample.Length));
                var path = SafePaths.Child(job.AttachmentDestination, item.Path);
                if (!File.Exists(path) || new FileInfo(path).Length != item.Length || await BackupEngine.HashAsync(path, ct) != item.Sha256)
                    run.Warnings.Add("Attachment missing or changed since this backup: " + item.Path);
                else catalog.Verified(item.Path);
            }
        }
        run.Detail = "Tested backup " + Path.GetFileName(archive) + ". Database extracted, hashed and checked.";
        if (!job.KeepSuccessfulTest && run.Warnings.Count == 0) SafePaths.DeleteOwnedDirectory(directory, job.Id);
    }
    /// <summary>Requires an exclusive profile lease. The UI must confirm the exact plan first.</summary>
    public async Task RestoreAsync(string archive, string destination, RestoreMode mode, IReadOnlyCollection<string> selected, bool restoreProfileFiles, RunResult run, CancellationToken ct)
    {
        SafePaths.NoLinks(destination);
        if (File.Exists(Path.Combine(destination, ".restore-in-progress.json"))) throw new IOException("Recover the interrupted restoration before starting a new one.");
        SafePaths.Separate(destination, job.Destination);
        if (!string.IsNullOrWhiteSpace(job.AttachmentDestination)) SafePaths.Separate(destination, job.AttachmentDestination);
        var manifest = BackupEngine.ReadManifest(archive);
        if (manifest.JobId != job.Id) throw new InvalidDataException("Backup job mismatch.");
        ValidateFileList(manifest);
        Directory.CreateDirectory(destination);
        var staged = SafePaths.Child(destination, ".restore-staging-" + run.Id);
        var rollback = SafePaths.Child(destination, ".restore-previous-" + run.Id);
        var replacements = new List<(string Source, string Relative)>();
        if (mode is RestoreMode.Database or RestoreMode.DatabaseAndAttachments)
        {
            await ExtractAsync(archive, staged, ct);
            foreach (var file in manifest.Files.Where(x => restoreProfileFiles || x.Path == manifest.DatabaseName))
                replacements.Add((SafePaths.Child(staged, file.Path), file.Path));
            var liveDatabase = SafePaths.Child(destination, manifest.DatabaseName);
            if (File.Exists(liveDatabase)) SqliteValidation.Checkpoint(liveDatabase);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                if (File.Exists(liveDatabase + suffix)) throw new IOException("SQLite sidecar remains present. Close all programs using the target database before restoring.");
        }
        else
        {
            Directory.CreateDirectory(staged); File.WriteAllText(SafePaths.Child(staged, ".backup-owner"), job.Id);
        }
        if (mode != RestoreMode.Database)
        {
            if (!manifest.IncludesAttachments) throw new InvalidOperationException("This backup has no attachment inventory.");
            var inventory = manifest.Attachments.Where(x => mode != RestoreMode.SelectedAttachments || selected.Contains(x.Path, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (mode == RestoreMode.SelectedAttachments && (inventory.Length == 0 || inventory.Length != selected.Distinct(StringComparer.OrdinalIgnoreCase).Count())) throw new InvalidDataException("Select valid attachment paths from the manifest.");
            SafePaths.CheckSpace(destination, inventory.Sum(x => x.Length));
            foreach (var file in inventory)
            {
                ct.ThrowIfCancellationRequested(); progress?.Report(new("Preparing attachment " + file.Path));
                var source = SafePaths.Child(job.AttachmentDestination, file.Path);
                if (!File.Exists(source)) { run.Warnings.Add("Unavailable attachment: " + file.Path); continue; }
                var target = SafePaths.Child(staged, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await CopyAsync(source, target, ct);
                var hash = await BackupEngine.HashAsync(target, ct);
                if (hash != await BackupEngine.HashAsync(source, ct)) throw new IOException("Attachment copy verification failed.");
                if (hash != file.Sha256) run.Warnings.Add("Restored current attachment version (different from historical database): " + file.Path);
                replacements.Add((target, file.Path));
            }
        }
        ct.ThrowIfCancellationRequested();
        if (replacements.Select(x => x.Relative.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != replacements.Count)
            throw new InvalidDataException("Duplicate restore targets.");
        Directory.CreateDirectory(rollback); File.WriteAllText(SafePaths.Child(rollback, ".backup-owner"), job.Id);
        var journalPath = SafePaths.Child(destination, ".restore-in-progress.json");
        var journal = new RestoreJournal(job.Id, run.Id,
            replacements.Select(x => new RestoreJournalEntry(x.Relative, File.Exists(SafePaths.Child(destination, x.Relative)))).ToList());
        JsonFiles.Write(journalPath, journal);
        var applied = new List<(string Target, string? Previous)>();
        try
        {
            // Short commit phase cannot be cancelled halfway. Roll back on any failure.
            foreach (var item in replacements)
            {
                var target = SafePaths.Child(destination, item.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? previous = null;
                if (File.Exists(target))
                {
                    previous = SafePaths.Child(rollback, item.Relative); Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                    File.Move(target, previous);
                }
                applied.Add((target, previous));
                File.Move(item.Source, target); run.FilesCopied++;
            }
        }
        catch
        {
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                if (File.Exists(item.Target)) File.Delete(item.Target);
                if (item.Previous != null) File.Move(item.Previous, item.Target);
            }
            File.Delete(journalPath);
            throw;
        }
        if (mode is RestoreMode.Database or RestoreMode.DatabaseAndAttachments)
            JsonFiles.Write(SafePaths.Child(destination, ".backup-restored-profile.json"), new { BackupId = manifest.BackupId, RestoredUtc = DateTimeOffset.UtcNow });
        File.Delete(journalPath);
        run.Artifact = rollback; run.Detail = "Restored to " + destination + ". Previous files retained at " + rollback;
        SafePaths.DeleteOwnedDirectory(staged, job.Id);
    }
    private static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        await input.CopyToAsync(output, ct);
    }
    private void ValidateFileList(Manifest manifest)
    {
        if (!manifest.DatabaseName.Equals(job.DatabaseName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Database name does not match this job.");
        foreach (var file in manifest.Files.Concat(manifest.Attachments))
        {
            var relative = file.Path.Replace('\\', '/');
            if (relative.Split('/').Any(x => x.StartsWith(".backup", StringComparison.OrdinalIgnoreCase) || x.StartsWith(".restore", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Backup contains a reserved coordinator path.");
            _ = SafePaths.Child(job.ProfileDirectory, relative);
        }
    }

    public static void RecoverInterrupted(string destination, string jobId)
    {
        SafePaths.NoLinks(destination);
        var journalPath = SafePaths.Child(destination, ".restore-in-progress.json");
        var journal = JsonFiles.Read<RestoreJournal>(journalPath);
        if (journal.JobId != jobId || !Guid.TryParse(journal.RunId, out _)) throw new InvalidDataException("Recovery journal does not match this job.");
        var rollback = SafePaths.Child(destination, ".restore-previous-" + journal.RunId);
        var staged = SafePaths.Child(destination, ".restore-staging-" + journal.RunId);
        if (File.ReadAllText(SafePaths.Child(rollback, ".backup-owner")) != jobId || File.ReadAllText(SafePaths.Child(staged, ".backup-owner")) != jobId)
            throw new InvalidDataException("Recovery folders are not owned by this job.");
        using var lease = new FileStream(Path.Combine(destination, ".backup-access.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var targets = journal.Entries.Select(x => (Entry: x, Target: SafePaths.Child(destination, x.Relative), Previous: SafePaths.Child(rollback, x.Relative), Stage: SafePaths.Child(staged, x.Relative))).ToArray();
        foreach (var item in targets.Reverse())
        {
            if (File.Exists(item.Previous))
            {
                if (File.Exists(item.Target))
                {
                    var retained = item.Target + ".interrupted-" + journal.RunId;
                    if (File.Exists(retained)) throw new IOException("Recovery preservation file already exists: " + retained);
                    File.Move(item.Target, retained);
                }
                File.Move(item.Previous, item.Target);
            }
            else if (!item.Entry.HadOriginal && !File.Exists(item.Stage) && File.Exists(item.Target))
                File.Move(item.Target, item.Target + ".interrupted-" + journal.RunId);
        }
        File.Delete(journalPath);
    }
}

public sealed record RestoreJournal(string JobId, string RunId, List<RestoreJournalEntry> Entries);
public sealed record RestoreJournalEntry(string Relative, bool HadOriginal);
