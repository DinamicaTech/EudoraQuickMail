using QuickMail.Models;
using QuickMail.ViewModels;

namespace QuickMail.Tests;

public sealed class FolderSelectionProgressTests
{
    private static MainViewModel Create() => new(new StubImapMailService(), new StubAccountService(),
        new StubCredentialService(), new StubLocalStoreService(), new StubOAuthService(),
        new StubSyncService(), new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
        new StubRuleService(), new StubSmtpService(), uiDispatcher: new StubUiDispatcher());

    [Fact]
    public async Task SelectionSignalsItsFolderAndAlwaysCompletesTheIndicatorLifetime()
    {
        using var vm = Create();
        var folder = new MailFolderModel { FullName = "Out", DisplayName = "Out" };
        Task? lifetime = null;
        vm.FolderSelectionStarted += (selected, completion) =>
        {
            Assert.Same(folder, selected);
            Assert.False(completion.IsCompleted);
            lifetime = completion;
        };
        await vm.SelectFolderCommand.ExecuteAsync(folder);
        Assert.NotNull(lifetime);
        Assert.True(lifetime.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task InvalidSelectionDoesNotStartIndicator()
    {
        using var vm = Create();
        var calls = 0;
        vm.FolderSelectionStarted += (_, _) => calls++;
        await vm.SelectFolderCommand.ExecuteAsync(null);
        await vm.SelectFolderCommand.ExecuteAsync(new MailFolderModel { IsHeader = true });
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExceptionalExitStillCompletesIndicatorLifetime()
    {
        using var vm = Create();
        Task? lifetime = null;
        vm.FolderSelectionStarted += (_, completion) =>
        {
            lifetime = completion;
            throw new InvalidOperationException("simulated selection failure");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.SelectFolderCommand.ExecuteAsync(
            new MailFolderModel { FullName = "Out", DisplayName = "Out" }));
        Assert.NotNull(lifetime);
        Assert.True(lifetime.IsCompletedSuccessfully);
    }
}
