using DinamicaBackup;

public sealed class ManualCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "backup-command-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _id = Guid.NewGuid().ToString("N");
    private string JobPath => Path.Combine(_root, "job.json");
    private ManualCommands Store => new(JobPath, _id);
    public ManualCommandTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".backup-owner"), _id);
        File.WriteAllText(JobPath, "{}");
    }
    [Fact] public void CommandIsClaimedExactlyOnce()
    {
        var request = Store.Submit("Backup", ManualCommands.Hash(JobPath));
        Assert.Equal(request, Store.Claim());
        Assert.Null(Store.Claim());
        Assert.Throws<IOException>(() => Store.Submit("Backup", "hash"));
        Store.Update(request.Id, "Completed", "done");
        Assert.Null(Store.Claim());
        Assert.NotEqual(request.Id, Store.Submit("Backup", "hash").Id);
        Assert.Equal("Completed", Store.ReadStatus(request.Id)!.State);
    }
    [Fact] public void PendingCancellationPreventsExecution()
    {
        var request = Store.Submit("TestDatabaseAccess", "hash");
        Store.Cancel(request.Id);
        Assert.Null(Store.Claim());
        Assert.Equal("Cancelled", Store.ReadStatus()!.State);
    }
    [Fact] public void RunningCancellationDoesNotPrematurelySignalCompletion()
    {
        var request = Store.Submit("Backup", "hash"); Store.Claim(); Store.Cancel(request.Id);
        Assert.True(Store.CancellationRequested(request.Id));
        Assert.False(Store.ReadStatus()!.Finished);
        Store.Update(request.Id, "Cancelled", "cleaned up", reopen: true);
        Assert.True(Store.ReadStatus()!.Finished);
        Assert.True(Store.ReadStatus()!.ReopenApplication);
    }
    [Fact] public void UnrelatedCancellationCannotCancelCurrentRequest()
    {
        var request = Store.Submit("Backup", "hash");
        Store.Cancel(Guid.NewGuid().ToString("N"));
        Assert.False(Store.CancellationRequested(request.Id));
    }
    [Fact] public void TerminalStatusCannotBeOverwrittenByLateProgress()
    {
        var request = Store.Submit("Backup", "hash"); Store.Claim();
        Store.Update(request.Id, "Completed", "done", result: new RunResult { Status = "Success" });
        Store.Update(request.Id, "Running", "late progress");
        Assert.Equal("done", Store.ReadStatus()!.Detail);
        Assert.Equal("Success", Store.ReadStatus()!.Result!.Status);
    }
    [Fact] public void ExpiredRequestNeverRuns()
    {
        var request = Store.Submit("Backup", "hash");
        JsonFiles.Write(Path.Combine(_root, ".backup-manual", _id, "request.json"), request with { ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1) });
        Assert.Null(Store.Claim());
        Assert.Equal("Expired", Store.ReadStatus()!.State);
    }
    [Fact] public void DeadWorkerDoesNotReplayCommand()
    {
        var request = Store.Submit("Backup", "hash"); Store.Claim();
        JsonFiles.Write(Path.Combine(_root, ".backup-manual", _id, "status.json"), Store.ReadStatus()! with { WorkerPid = int.MaxValue });
        Store.RecoverAbandoned();
        Assert.Equal("Failed", Store.ReadStatus()!.State);
        Assert.Null(Store.Claim());
    }
    [Fact] public void LivingWorkerIsNotMarkedAbandoned()
    {
        Store.Submit("Backup", "hash"); Store.Claim(); Store.RecoverAbandoned();
        Assert.Equal("Running", Store.ReadStatus()!.State);
    }
    [Theory]
    [InlineData("Restore")]
    [InlineData("powershell anything")]
    [InlineData("../Backup")]
    public void OnlySupportedOperationsCanBeSubmitted(string operation) =>
        Assert.Throws<ArgumentException>(() => Store.Submit(operation, "hash"));
    [Fact] public void ConfigurationHashDetectsEdits()
    {
        var hash = ManualCommands.Hash(JobPath); File.WriteAllText(JobPath, "{\"changed\":true}");
        Assert.NotEqual(hash, ManualCommands.Hash(JobPath));
    }
    [Fact] public void ProbePreservesExistingFilesAndRemovesOnlyItsOwnFile()
    {
        var original = File.ReadAllText(JobPath);
        var before = Directory.GetFiles(_root).Order().ToArray();
        DirectoryAccessProbe.Test(_root, TestContext.Current.CancellationToken);
        Assert.Equal(before, Directory.GetFiles(_root).Order().ToArray());
        Assert.Equal(original, File.ReadAllText(JobPath));
    }
    [Fact] public void CancelledProbeCreatesNothing()
    {
        var before = Directory.GetFiles(_root).Length;
        Assert.Throws<OperationCanceledException>(() => DirectoryAccessProbe.Test(_root, new CancellationToken(true)));
        Assert.Equal(before, Directory.GetFiles(_root).Length);
    }
    [Fact] public void ProbeDoesNotCreateMissingDestination()
    {
        var missing = Path.Combine(_root, "missing");
        Assert.Throws<DirectoryNotFoundException>(() => DirectoryAccessProbe.Test(missing, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(missing));
    }
    public void Dispose() => SafePaths.DeleteOwnedDirectory(_root, _id);
}
