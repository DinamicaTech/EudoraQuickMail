using System.IO;
using DinamicaBackup;
using DinamicaBackupAgent;

public sealed class ManualWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "backup-worker-test-" + Guid.NewGuid().ToString("N"));
    private readonly BackupJob _job = new();
    private string JobPath => Path.Combine(_root, "job.json");
    public ManualWorkerTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".backup-owner"), _job.Id);
        _job.ProfileDirectory = Path.Combine(_root, "profile");
        JsonFiles.Write(JobPath, _job);
    }
    [Fact] public async Task ManualRequestIsHandledEvenWithAllSchedulesDisabled()
    {
        var queue = new ManualCommands(JobPath, _job.Id);
        var request = queue.Submit("TestDatabaseAccess", ManualCommands.Hash(JobPath));
        // Invalid/missing UNC destination reports failure without requiring any DB or schedule.
        Assert.True(await ManualWorker.ProcessAsync(_job, JobPath, TestContext.Current.CancellationToken));
        Assert.Equal("Failed", queue.ReadStatus(request.Id)!.State);
        Assert.Contains("UNC", queue.ReadStatus(request.Id)!.Detail);
        Assert.False(await ManualWorker.ProcessAsync(_job, JobPath, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(_job.ProfileDirectory));
    }
    [Fact] public async Task ChangedConfigurationNeverExecutesTheRequestedBackup()
    {
        var queue = new ManualCommands(JobPath, _job.Id);
        var request = queue.Submit("Backup", ManualCommands.Hash(JobPath));
        _job.Name += "changed"; JsonFiles.Write(JobPath, _job);
        Assert.True(await ManualWorker.ProcessAsync(_job, JobPath, TestContext.Current.CancellationToken));
        Assert.Contains("configuration changed", queue.ReadStatus(request.Id)!.Detail);
        Assert.False(Directory.Exists(_job.ProfileDirectory));
    }
    public void Dispose() => SafePaths.DeleteOwnedDirectory(_root, _job.Id);
}
