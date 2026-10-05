using DinamicaBackup;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text.Json;

public sealed class BackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dinamica-backup-test-" + Guid.NewGuid().ToString("N"));
    private readonly BackupJob _job;
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class CapturedProgress : IProgress<ProgressInfo>
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Stages = new();
        public void Report(ProgressInfo value) => Stages.Enqueue(value.Stage);
    }
    [Fact] public void SQLitePreparationReportsIndividualStages()
    {
        var progress = new CapturedProgress();
        SqliteValidation.Checkpoint(Path.Combine(_job.ProfileDirectory, "mail.db"), progress);
        SqliteValidation.Inspect(Path.Combine(_job.ProfileDirectory, "mail.db"), _job.Probes, false, Ct, progress);
        Assert.Contains(progress.Stages, x => x.Contains("checkpoint"));
        Assert.Contains(progress.Stages, x => x.Contains("quick_check"));
        Assert.Contains(progress.Stages, x => x.Contains("foreign-key"));
        Assert.Contains(progress.Stages, x => x.Contains("schema version"));
        Assert.Contains(progress.Stages, x => x.Contains("validation query — Messages"));
        Assert.All(progress.Stages, x => Assert.Contains("Elapsed:", x));
    }
    public BackupTests()
    {
        Directory.CreateDirectory(_root);
        _job = new BackupJob { ProfileDirectory = Path.Combine(_root, "profile"), Destination = Path.Combine(_root, "zip"), AttachmentDestination = Path.Combine(_root, "attachments"), RestoreTestDirectory = Path.Combine(_root, "tests"), CopyAttachments = true, ProfileFiles = ["accounts.json"], Probes = [new("Messages", "SELECT COUNT(*) FROM Messages")], AttachmentSampleSize = 100, TestPreviousDay = false };
        File.WriteAllText(Path.Combine(_root, ".backup-owner"), _job.Id);
        Directory.CreateDirectory(_job.ProfileDirectory); Directory.CreateDirectory(_job.Destination);
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE Messages(Id INTEGER PRIMARY KEY,Subject TEXT); INSERT INTO Messages VALUES(1,'test'); PRAGMA user_version=9;"; command.ExecuteNonQuery();
        File.WriteAllText(Path.Combine(_job.ProfileDirectory, "accounts.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_job.ProfileDirectory, "Attachments", "2026"));
        File.WriteAllText(Path.Combine(_job.ProfileDirectory, "Attachments", "2026", "invoice.pdf"), "fixture PDF");
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_job.ProfileDirectory, "mail.db"), Pooling = false }.ToString()); db.Open(); return db;
    }
    [Fact] public void ExternalOnlyConfigurationDoesNotRequireBackupDestination()
    {
        _job.Destination = ""; _job.AttachmentDestination = "";
        SafePaths.Validate(_job, false);
        Assert.Throws<InvalidDataException>(() => SafePaths.Validate(_job));
    }
    [Fact] public void DetectInstallationUsesDataPathFile()
    {
        var install = Path.Combine(_root, "app"); Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "EudoraQM.exe"), "");
        File.WriteAllText(Path.Combine(install, "QuickMailDataPath.txt"), _job.ProfileDirectory);
        var detected = ApplicationDetection.DetectEqm(install, new BackupJob());
        Assert.Equal(_job.ProfileDirectory, detected.Profile);
        Assert.Equal(Path.Combine(install, "EudoraQM.exe"), detected.Executable);
    }
    [Fact] public void DetectInstallationPreservesExplicitLiveProfile()
    {
        var install = Path.Combine(_root, "app"); Directory.CreateDirectory(install);
        _job.ApplicationPath = Path.Combine(install, "EudoraQM.exe");
        File.WriteAllText(_job.ApplicationPath, "");
        File.WriteAllText(Path.Combine(install, "QuickMailDataPath.txt"), Path.Combine(_root, "other"));
        Assert.Equal(_job.ProfileDirectory, ApplicationDetection.DetectEqm(install, _job).Profile);
    }
    [Fact] public void DefaultEqmProbeReadsLatestDate()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE MessageSummary(date_ticks INTEGER); INSERT INTO MessageSummary VALUES (123), (456);";
        command.ExecuteNonQuery(); command.CommandText = ApplicationDetection.EqmProbes().Single().Sql;
        Assert.Equal(456L, command.ExecuteScalar());
    }
    [Theory]
    [InlineData(@"\\nas\share\folder", @"\\nas\share")]
    [InlineData(@"C:\backup", "")]
    [InlineData(@"\\nas", "")]
    public void CredentialsUseShareRoot(string path, string expected) => Assert.Equal(expected, ApplicationDetection.UncShare(path));
    [Fact] public void IndependentCredentialsRoundTrip()
    {
        _job.NasCredential = "database-user"; _job.AttachmentNasCredential = "attachment-user";
        var copy = JsonSerializer.Deserialize<BackupJob>(JsonSerializer.Serialize(_job))!;
        Assert.Equal("database-user", copy.NasCredential); Assert.Equal("attachment-user", copy.AttachmentNasCredential);
    }
    private async Task<RunResult> Backup()
    {
        var result = await new Runner(_job).RunAsync("Backup", Ct);
        Assert.Equal("Success", result.Status); return result;
    }
    public void Dispose() => SafePaths.DeleteOwnedDirectory(_root, _job.Id);

    [Fact] public async Task ZipAndNativeMirrorRestoreAndVerify()
    {
        var backup = await Backup();
        var manifest = BackupEngine.ReadManifest(backup.Artifact);
        Assert.Equal(9, manifest.SchemaVersion); Assert.Equal("1", manifest.ProbeValues["Messages"]);
        Assert.Single(manifest.Attachments); Assert.Equal(2, manifest.Files.Count);
        Assert.True(File.Exists(Path.Combine(_job.AttachmentDestination, "Attachments", "2026", "invoice.pdf")));
        var test = await new Runner(_job).RunAsync("Test", Ct, backup.Artifact);
        Assert.Equal("Success", test.Status); Assert.False(Directory.Exists(test.Artifact));
    }
    [Fact] public async Task SampleDetectsMissingFileAndRetainsRestoredDatabase()
    {
        var backup = await Backup();
        File.Delete(SafePaths.Child(_job.AttachmentDestination, "Attachments/2026/invoice.pdf"));
        var test = await new Runner(_job).RunAsync("Test", Ct, backup.Artifact);
        Assert.Equal("Warning", test.Status); Assert.Single(test.Warnings); Assert.True(File.Exists(Path.Combine(test.Artifact, "mail.db")));
    }
    [Fact] public async Task MissingSourceDirectoryNeverPurgesMirror()
    {
        await Backup(); _job.DeleteMissingAttachments = true; _job.MaximumDeletionPercent = 100;
        Directory.Move(Path.Combine(_job.ProfileDirectory, "Attachments"), Path.Combine(_job.ProfileDirectory, "Renamed"));
        var result = await new Runner(_job).RunAsync("Backup", Ct);
        Assert.Equal("Warning", result.Status); Assert.True(File.Exists(SafePaths.Child(_job.AttachmentDestination, "Attachments/2026/invoice.pdf")));
    }
    [Fact] public async Task PermittedDeletionRemovesOnlyCataloguedMissingAttachments()
    {
        await Backup(); _job.DeleteMissingAttachments = true; _job.MaximumDeletionPercent = 100;
        File.Delete(SafePaths.Child(_job.ProfileDirectory, "Attachments/2026/invoice.pdf"));
        File.WriteAllText(Path.Combine(_job.AttachmentDestination, "unrelated.txt"), "keep");
        await Backup();
        Assert.False(File.Exists(SafePaths.Child(_job.AttachmentDestination, "Attachments/2026/invoice.pdf")));
        Assert.True(File.Exists(Path.Combine(_job.AttachmentDestination, "unrelated.txt")));
    }
    [Fact] public async Task UnchangedAttachmentsAreNotRecopied()
    {
        await Backup(); var second = await Backup(); Assert.Equal(2, second.FilesCopied);
    }
    [Fact] public async Task HistoryRefreshReadsReceiptsWithoutWritingToDestination()
    {
        var backup = await Backup();
        var catalogPath = Path.Combine(_job.Destination, ".dinamica-backup", _job.Id, "backup-catalog.db");
        File.Delete(catalogPath);
        var history = Catalog.ReadRecentReceipts(_job, 100);
        Assert.Contains(history, run => run.Id == backup.Id);
        Assert.False(File.Exists(catalogPath));
        var local = Catalog.ReadRecentReceipts(_job, 100, includeDestination: false);
        Assert.Contains(local, run => run.Id == backup.Id);
    }
    [Fact] public async Task OptionalLegacyEudoraSourceIsMirroredSeparately()
    {
        var eudora = Path.Combine(_root, "Eudora");
        var attach = Path.Combine(eudora, "Attach2026");
        Directory.CreateDirectory(attach);
        File.WriteAllText(Path.Combine(attach, "legacy.pdf"), "legacy fixture");
        JsonFiles.Write(Path.Combine(_job.ProfileDirectory, "eudora-import-settings.json"),
            new { SourceRoot = eudora, AttachmentMode = "keep", ImportedAt = DateTimeOffset.UtcNow });
        Assert.NotNull(LegacyAttachmentSource.Detect(_job.ProfileDirectory));
        _job.IncludeLegacyEudoraAttachments = true;
        var backup = await Backup();
        var manifest = BackupEngine.ReadManifest(backup.Artifact);
        Assert.Equal(eudora, manifest.LegacyAttachmentSourceRoot);
        Assert.Contains(manifest.Attachments, file => file.Path == "LegacyEudora/Attach2026/legacy.pdf");
        Assert.True(File.Exists(Path.Combine(_job.AttachmentDestination, "LegacyEudora", "Attach2026", "legacy.pdf")));
        Assert.Contains(manifest.Files, file => file.Path == "eudora-import-settings.json");
        _job.IncludeLegacyEudoraAttachments = false;
        _job.DeleteMissingAttachments = true;
        _job.MaximumDeletionPercent = 100;
        var withoutLegacy = await Backup();
        Assert.DoesNotContain(BackupEngine.ReadManifest(withoutLegacy.Artifact).Attachments,
            file => file.Path.StartsWith(LegacyAttachmentSource.BackupPrefix, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(_job.AttachmentDestination, "LegacyEudora", "Attach2026", "legacy.pdf")));
    }
    [Fact] public async Task RestoreTestRecordsIndividualProbeAndAttachmentChecks()
    {
        var backup = await Backup();
        var result = await new Runner(_job).RunAsync("Test", Ct, backup.Artifact);
        Assert.Equal("Success", result.Status);
        Assert.Contains(result.VerificationDetails, detail => detail.Contains("Messages", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.VerificationDetails, detail => detail.Contains("invoice.pdf", StringComparison.OrdinalIgnoreCase));
    }
    [Fact] public async Task ExternalBackupWindowKeepsLegacyKindButDoesNotSendCompletionEmail()
    {
        _job.Notification.Enabled = true;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var result = await new Runner(_job).RunAsync("Maintenance", cancel.Token);
        Assert.Equal("Maintenance", result.Kind);
        Assert.Equal("External backup window", result.DisplayKind);
        Assert.Equal("Cancelled", result.Status);
        Assert.Empty(result.NotificationError);
    }
    [Fact] public async Task ModifiedAttachmentReplacesCurrentMirror()
    {
        await Backup(); File.WriteAllText(SafePaths.Child(_job.ProfileDirectory, "Attachments/2026/invoice.pdf"), "modified fixture content");
        await Backup(); Assert.Equal("modified fixture content", File.ReadAllText(SafePaths.Child(_job.AttachmentDestination, "Attachments/2026/invoice.pdf")));
    }
    [Fact] public async Task CancelledRunPublishesNoArchiveAndReleasesLease()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var run = await new Runner(_job).RunAsync("Backup", cancel.Token);
        Assert.Equal("Cancelled", run.Status); Assert.Empty(Directory.GetFiles(_job.Destination, "*.zip"));
        using var app = ProfileLease.OpenApplication(_job.ProfileDirectory);
    }
    [Fact] public async Task LiveApplicationPreventsBackupWithoutForcedClose()
    {
        _job.ShutdownTimeoutSeconds = 1;
        using var app = ProfileLease.OpenApplication(_job.ProfileDirectory);
        var run = await new Runner(_job).RunAsync("Backup", Ct);
        Assert.Equal("Failed", run.Status); Assert.Contains("timeout", run.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(_job.Destination, "*.zip"));
        Assert.Null(ProfileLease.Request(_job.ProfileDirectory));
    }
    [Fact] public async Task ExclusiveLeasePreventsApplicationStartup()
    {
        var (lease, _) = await ProfileLease.AcquireAsync(_job, null, Ct);
        using (lease) Assert.Throws<IOException>(() => ProfileLease.OpenApplication(_job.ProfileDirectory));
        using var app = ProfileLease.OpenApplication(_job.ProfileDirectory);
    }
    [Fact] public async Task DatabaseRestorePreservesPreviousFiles()
    {
        var backup = await Backup();
        using (var db = Open()) { using var command = db.CreateCommand(); command.CommandText = "INSERT INTO Messages VALUES(2,'later')"; command.ExecuteNonQuery(); }
        var run = await new Runner(_job).RunAsync("Restore", Ct, backup.Artifact, _job.ProfileDirectory);
        Assert.Equal("Success", run.Status);
        using var after = Open(); using var read = after.CreateCommand(); read.CommandText = "SELECT COUNT(*) FROM Messages"; Assert.Equal(1L, read.ExecuteScalar());
        Assert.True(File.Exists(Path.Combine(run.Artifact, "mail.db")));
    }
    [Fact] public async Task SingleAttachmentRestoreDoesNotReplaceDatabase()
    {
        var backup = await Backup(); var target = Path.Combine(_root, "single");
        var run = await new Runner(_job).RunAsync("Restore", Ct, backup.Artifact, target, RestoreMode.SelectedAttachments, ["Attachments/2026/invoice.pdf"]);
        Assert.True(run.Status == "Success", run.Detail); Assert.Equal(1, run.FilesCopied);
        Assert.True(File.Exists(SafePaths.Child(target, "Attachments/2026/invoice.pdf"))); Assert.False(File.Exists(Path.Combine(target, "mail.db")));
    }
    [Fact] public async Task CorruptZipCannotOverwriteLiveDatabase()
    {
        var backup = await Backup(); var before = await BackupEngine.HashAsync(Path.Combine(_job.ProfileDirectory, "mail.db"), Ct);
        using (var zip = ZipFile.Open(backup.Artifact, ZipArchiveMode.Update)) { zip.GetEntry("profile/mail.db")!.Delete(); using var entry = new StreamWriter(zip.CreateEntry("profile/mail.db").Open()); entry.Write("corrupt"); }
        var result = await new Runner(_job).RunAsync("Restore", Ct, backup.Artifact, _job.ProfileDirectory);
        Assert.Equal("Failed", result.Status); Assert.Equal(before, await BackupEngine.HashAsync(Path.Combine(_job.ProfileDirectory, "mail.db"), Ct));
    }
    [Theory]
    [InlineData("../escape")][InlineData("/absolute")][InlineData("a/../../escape")][InlineData("a:stream")]
    public void RejectsUnsafePaths(string path) => Assert.Throws<InvalidDataException>(() => SafePaths.Child(_root, path));
    [Fact] public void RejectsOverlappingDestinations()
    {
        _job.Destination = Path.Combine(_job.ProfileDirectory, "backup"); Assert.Throws<InvalidDataException>(() => SafePaths.Validate(_job));
    }
    [Fact] public async Task RetentionAlwaysKeepsOneCompleteBackup()
    {
        _job.KeepDaily = 1; _job.KeepWeekly = 0; _job.KeepMonthly = 0;
        await Backup(); var latest = await Backup();
        Assert.Single(Directory.GetFiles(_job.Destination, "*.zip")); Assert.True(File.Exists(latest.Artifact));
    }
    [Fact] public async Task QueryMustBeScalarAndReadOnly()
    {
        _job.Probes = [new("Bad", "SELECT 1; DELETE FROM Messages")];
        var run = await new Runner(_job).RunAsync("Backup", Ct); Assert.Equal("Failed", run.Status); Assert.Empty(Directory.GetFiles(_job.Destination, "*.zip"));
    }
    [Fact] public async Task CatalogCanBeRebuiltFromReceipts()
    {
        var backup = await Backup();
        File.Delete(Path.Combine(_job.Destination, ".dinamica-backup", _job.Id, "backup-catalog.db"));
        using var catalog = new Catalog(_job); Assert.Equal(backup.Id, Assert.Single(catalog.Recent()).Id);
    }
    [Fact] public void MonthlyScheduleClampsToLastDay()
    {
        var schedule = new Schedule { Enabled = true, Frequency = "Monthly", DayOfMonth = 31, Time = "02:00" };
        Assert.Equal(new DateTime(2026, 2, 28, 2, 0, 0), schedule.LatestOccurrence(new DateTime(2026, 2, 28, 3, 0, 0)));
    }
    [Fact] public void MissedExecutionsAreEnumeratedIndividually()
    {
        var schedule = new Schedule { Enabled = true, Time = "02:00" };
        var pending = schedule.Pending(new DateTime(2026, 9, 27, 3, 0, 0), new DateTime(2026, 9, 24, 2, 0, 0));
        Assert.Equal(3, pending.Count); Assert.Equal(25, pending[0].Day); Assert.Equal(27, pending[^1].Day);
    }
    [Fact] public async Task DestinationConnectionFailureIsRecordedLocally()
    {
        var runner = new Runner(_job) { ConnectDestination = () => throw new IOException("NAS unavailable") };
        var run = await runner.RunAsync("Backup", Ct);
        Assert.Equal("Failed", run.Status); Assert.Equal("NAS unavailable", run.Detail);
        Assert.True(File.Exists(Path.Combine(_job.ProfileDirectory, "backup-agent-last-run.json")));
    }
    [Fact] public void InterruptedRestoreBlocksStartupAndCanRollBack()
    {
        var runId = Guid.NewGuid().ToString("N");
        var rollback = Path.Combine(_job.ProfileDirectory, ".restore-previous-" + runId);
        var staged = Path.Combine(_job.ProfileDirectory, ".restore-staging-" + runId);
        Directory.CreateDirectory(rollback); Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(rollback, ".backup-owner"), _job.Id); File.WriteAllText(Path.Combine(staged, ".backup-owner"), _job.Id);
        File.Move(Path.Combine(_job.ProfileDirectory, "accounts.json"), Path.Combine(rollback, "accounts.json"));
        File.WriteAllText(Path.Combine(_job.ProfileDirectory, "accounts.json"), "new partial profile");
        JsonFiles.Write(Path.Combine(_job.ProfileDirectory, ".restore-in-progress.json"), new RestoreJournal(_job.Id, runId, [new("accounts.json", true)]));
        Assert.Throws<IOException>(() => ProfileLease.OpenApplication(_job.ProfileDirectory));
        RestoreEngine.RecoverInterrupted(_job.ProfileDirectory, _job.Id);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_job.ProfileDirectory, "accounts.json")));
        Assert.Equal("new partial profile", File.ReadAllText(Path.Combine(_job.ProfileDirectory, "accounts.json.interrupted-" + runId)));
        using var app = ProfileLease.OpenApplication(_job.ProfileDirectory);
    }
    [Fact] public async Task ForeignKeyViolationPreventsPublishingBackup()
    {
        using (var db = Open()) { using var command = db.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=OFF; CREATE TABLE Child(Parent INTEGER REFERENCES Messages(Id)); INSERT INTO Child VALUES(99)"; command.ExecuteNonQuery(); }
        var run = await new Runner(_job).RunAsync("Backup", Ct); Assert.Equal("Failed", run.Status); Assert.Contains("foreign key", run.Detail);
        Assert.Empty(Directory.GetFiles(_job.Destination, "*.zip"));
    }
    [Fact] public async Task SemanticallyInvalidProbeIsNotAccepted()
    {
        _job.Probes = [new("Wrong shape", "SELECT Id,Subject FROM Messages")];
        var run = await new Runner(_job).RunAsync("Backup", Ct); Assert.Equal("Failed", run.Status); Assert.Contains("one value", run.Detail);
    }
    [Fact] public async Task InterruptedRunIsReportedInHistory()
    {
        using (var catalog = new Catalog(_job)) catalog.Save(new RunResult { Id = "interrupted", Status = "Running" });
        await Backup();
        using var reopened = new Catalog(_job); Assert.Equal("Failed", reopened.Recent().Single(x => x.Id == "interrupted").Status);
    }
    [Fact] public async Task ExpensiveValidationQueryCanBeCancelled()
    {
        _job.Probes = [new("Unbounded", "SELECT (WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n) SELECT sum(x) FROM n)")];
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(200));
        var run = await new Runner(_job).RunAsync("Backup", cancel.Token);
        Assert.Equal("Cancelled", run.Status); Assert.Empty(Directory.GetFiles(_job.Destination, "*.zip"));
    }
}
