using Microsoft.Data.Sqlite;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using System.IO;

namespace QuickMail.Tests;

public sealed class ImapBodyBackfillTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuickMailBodyBackfill-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BackfillFetchesOneLogicalMessageAndIndexesEveryGmailCopy()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory));
        store.Initialize();
        var accountId = Guid.NewGuid();
        const string internetId = "<logical-message@example.com>";
        await store.UpsertSummariesAsync([
            Summary(accountId, "INBOX", "101", internetId),
            Summary(accountId, "All Mail", "202", internetId),
        ]);

        Assert.Equal(1, await store.CountPendingImapBodiesAsync([accountId], DateTimeOffset.UtcNow));
        var mail = new CountingBodyMailService();
        var worker = new ImapBodyBackfillService(mail, store);
        await worker.RunAsync([new AccountModel { Id = accountId, IsActive = true, BackendKind = BackendKind.ImapSmtp }]);

        Assert.Equal(1, mail.FetchCount);
        Assert.Equal("unique searchable body", (await store.LoadDetailAsync(accountId, "INBOX", "101"))?.PlainTextBody);
        Assert.Equal("unique searchable body", (await store.LoadDetailAsync(accountId, "All Mail", "202"))?.PlainTextBody);
        Assert.Equal(0, await store.CountPendingImapBodiesAsync([accountId], DateTimeOffset.UtcNow));
        var found = await store.SearchLocalMessagesAsync(new LocalSearchQuery("B:searchable", AccountIds: [accountId]));
        Assert.Equal(2, found.TotalMatches); // physical copies; aggregate views collapse them for display
        Assert.All(found.Messages, message => Assert.False(message.IsRead));
    }

    [Fact]
    public async Task SearchEverywhereIgnoresSelectedFolderAndSearchesEveryActiveAccount()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory));
        store.Initialize();
        var first = new AccountModel { Id = Guid.NewGuid(), IsActive = true, BackendKind = BackendKind.LocalArchive };
        var second = new AccountModel { Id = Guid.NewGuid(), IsActive = true, BackendKind = BackendKind.LocalArchive };
        await store.UpsertSummariesAsync([
            Summary(first.Id, "In", "1", "<one@example.com>", "globalneedle one"),
            Summary(second.Id, "Projects", "2", "<two@example.com>", "globalneedle two"),
        ]);
        using var vm = new MainViewModel(new StubImapMailService(), new StubAccountService(),
            new StubCredentialService(), store, new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(), uiDispatcher: new StubUiDispatcher());
        vm.LoadAccountList([first, second]);
        vm.SelectedFolder = new MailFolderModel { AccountId = first.Id, FullName = "In", DisplayName = "In" };

        await vm.RunQuickSearchAsync("globalneedle", everywhere: false);
        Assert.Single(vm.Messages);
        await vm.RunQuickSearchAsync("globalneedle", everywhere: true);
        Assert.Equal(2, vm.Messages.Count);
    }

    [Fact]
    public async Task LocalMutationRefreshRerunsTheActiveGlobalSavedSearch()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory));
        store.Initialize();
        var account = new AccountModel
        {
            Id = Guid.NewGuid(),
            IsActive = true,
            BackendKind = BackendKind.LocalArchive,
        };
        var first = Summary(account.Id, "In", "1", "<first@example.com>", "First today");
        first.Direction = MessageDirection.Incoming;
        var second = Summary(account.Id, "In", "2", "<second@example.com>", "Second today");
        second.Direction = MessageDirection.Incoming;
        await store.UpsertSummariesAsync([first, second]);
        var view = new SavedView
        {
            Name = "Received today",
            SearchQuery = "D:today;I",
            SearchEverywhere = true,
        };
        using var vm = new MainViewModel(new StubImapMailService(), new StubAccountService(),
            new StubCredentialService(), store, new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new FakeViewService([view]),
            new StubRuleService(), new StubSmtpService(), uiDispatcher: new StubUiDispatcher());
        vm.LoadAccountList([account]);

        await vm.SelectViewCommand.ExecuteAsync(view.Id.ToString());
        Assert.Equal(2, vm.Messages.Count);

        await store.DeleteSummariesAsync(first.AccountId, first.FolderName, [first.MessageId]);
        vm.RemoveMessagesFromActiveView([first]);
        await vm.RefreshAfterLocalMutationAsync("regression-test");

        Assert.Single(vm.Messages);
        Assert.Equal(second.MessageId, vm.Messages[0].MessageId);
        Assert.Equal("D:today;I", vm.SearchText);
        Assert.True(vm.SearchEverywhere);
        Assert.Same(view, vm.ActiveView);

        // Shift+Delete refreshes special-folder metadata and then re-enters the selected virtual
        // node. Set Sync Range uses that same FetchVirtualAsync path, so pin it here too.
        await vm.SetSyncDaysCommand.ExecuteAsync("180");
        Assert.Single(vm.Messages);
        Assert.Equal(second.MessageId, vm.Messages[0].MessageId);
        Assert.Same(view, vm.ActiveView);
    }

    private static MailMessageSummary Summary(Guid accountId, string folder, string uid,
        string internetId, string subject = "Body test") => new()
    {
        AccountId = accountId, FolderName = folder, MessageId = uid,
        InternetMessageId = internetId, From = "sender@example.com", To = "me@example.com",
        Subject = subject, Date = DateTimeOffset.UtcNow, IsRead = false,
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class CountingBodyMailService : StubImapMailServiceBase
    {
        public int FetchCount { get; private set; }

        public override Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId,
            string folderName, string messageId, CancellationToken ct = default)
        {
            FetchCount++;
            return Task.FromResult(new MailMessageDetail
            {
                AccountId = accountId, FolderName = folderName, MessageId = messageId,
                PlainTextBody = "unique searchable body", To = "me@example.com",
                InternetMessageId = "<logical-message@example.com>", IsRead = false,
            });
        }
    }
}
