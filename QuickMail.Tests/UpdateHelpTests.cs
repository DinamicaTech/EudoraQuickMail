using System.IO;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Tests;

public sealed class UpdateHelpTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuickMailUpdateTests-" + Guid.NewGuid().ToString("N"));
    private readonly MainViewModel _vm;
    private readonly UpdateStub _updates = new();

    public UpdateHelpTests()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory));
        store.Initialize();
        _vm = new MainViewModel(new StubImapMailService(), new StubAccountService(),
            new StubCredentialService(), store, new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
            new StubSmtpService(), uiDispatcher: new StubUiDispatcher(), updateCheckService: _updates);
    }

    [Fact]
    public async Task ManualCheckQueriesAgainAndOpensInstallDialog()
    {
        await _vm.CheckForUpdateInBackgroundAsync();
        Assert.Contains("No updates", _vm.UpdateAvailableText);
        _updates.Result = new UpdateInfo("9.0.0", "https://github.com/DinamicaTech/EudoraQuickMail/releases/tag/v9.0.0");
        var opened = false;
        _vm.UpdateDialogRequested += (_, _) => opened = true;
        await _vm.CheckForUpdatesNowCommand.ExecuteAsync(null);
        Assert.Equal(2, _updates.Calls);
        Assert.True(opened);
        Assert.Contains("9.0.0", _vm.UpdateAvailableText);
    }

    [Fact]
    public async Task DisabledCheckExplainsMarkerAndCanBeRetriedWithoutRestart()
    {
        _updates.DisabledReason = "Blocked by quickmail-standalone-install";
        await _vm.CheckForUpdatesNowCommand.ExecuteAsync(null);
        Assert.Equal(0, _updates.Calls);
        Assert.Contains("disabled", _vm.UpdateAvailableText);
        Assert.Contains("quickmail-standalone-install", _vm.UpdateCheckDetails);
        _updates.DisabledReason = null;
        await _vm.CheckForUpdatesNowCommand.ExecuteAsync(null);
        Assert.Equal(1, _updates.Calls);
        Assert.Contains("No updates", _vm.UpdateAvailableText);
    }

    [Fact]
    public async Task FailureDoesNotReportUpToDateOrOpenStaleUpdate()
    {
        _updates.Result = new UpdateInfo("9.0.0", "https://github.com/DinamicaTech/EudoraQuickMail/releases/tag/v9.0.0");
        await _vm.CheckForUpdateInBackgroundAsync();
        _updates.Error = new IOException("Network unavailable");
        var opened = false;
        _vm.UpdateDialogRequested += (_, _) => opened = true;
        await _vm.CheckForUpdatesNowCommand.ExecuteAsync(null);
        Assert.Contains("Could not check", _vm.UpdateAvailableText);
        Assert.Contains("Network unavailable", _vm.UpdateCheckDetails);
        Assert.Empty(_vm.UpdateReleaseUrl);
        Assert.False(opened);
        Assert.True(_vm.CheckForUpdatesNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConcurrentChecksAreCoalesced()
    {
        _updates.Pending = new TaskCompletionSource<UpdateInfo?>();
        var first = _vm.CheckForUpdateInBackgroundAsync();
        Assert.False(_vm.CheckForUpdatesNowCommand.CanExecute(null));
        await _vm.CheckForUpdateInBackgroundAsync();
        Assert.Equal(1, _updates.Calls);
        _updates.Pending.SetResult(null);
        await first;
        Assert.False(_vm.IsCheckingForUpdates);
    }

    public void Dispose()
    {
        _vm.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    private sealed class UpdateStub : IUpdateCheckService
    {
        public string? DisabledReason { get; set; }
        public UpdateInfo? Result { get; set; }
        public Exception? Error { get; set; }
        public TaskCompletionSource<UpdateInfo?>? Pending { get; set; }
        public int Calls { get; private set; }
        public bool SelfUpdatePending => Result != null;
        public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Error != null) throw Error;
            return Pending?.Task ?? Task.FromResult(Result);
        }
        public Task PrepareUpdateAsync(IProgress<string>? progress, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> RestartToUpdateAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
