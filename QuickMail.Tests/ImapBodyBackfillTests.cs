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

    [Fact]
    public async Task DateSearchRuleRemovalKeepsCollectionOrderAndNextMessage()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory));
        store.Initialize();
        var account = new AccountModel { Id = Guid.NewGuid(), IsActive = true, BackendKind = BackendKind.LocalArchive };
        var rows = Enumerable.Range(1, 4).Select(i => Summary(account.Id, "In", i.ToString(), $"<{i}@example.com>", $"Message {i}")).ToList();
        foreach (var row in rows) row.Date = new DateTimeOffset(2026, 7, int.Parse(row.MessageId), 12, 0, 0, TimeSpan.Zero);
        await store.UpsertSummariesAsync(rows);
        using var vm = new MainViewModel(new StubImapMailService(), new StubAccountService(),
            new StubCredentialService(), store, new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(), uiDispatcher: new StubUiDispatcher());
        vm.LoadAccountList([account]);
        vm.SelectedFolder = new MailFolderModel { AccountId = account.Id, FullName = "In", DisplayName = "In" };
        await vm.RunQuickSearchAsync("d>30/6/2026", false);
        Assert.Equal(4, vm.Messages.Count);
        var collection = vm.Messages;
        var removed = vm.Messages.Take(2).ToArray();
        vm.SelectedMessage = removed[0];
        vm.RemoveMessagesFromActiveView(removed);
        await vm.RefreshAfterLocalMutationAsync("apply-rules-to-selection", preserveMessageList: true);
        Assert.Same(collection, vm.Messages);
        Assert.Equal(new[] { "2", "1" }, vm.Messages.Select(m => m.MessageId));
        Assert.Same(vm.Messages[0], vm.SelectedMessage);
        Assert.Equal(2, vm.LocalTotalMessages);
        vm.ActiveSort = MessageSort.DateAscending;
        Assert.Equal(new[] { "1", "2" }, vm.Messages.Select(m => m.MessageId));
        // Removed rows still exist in this test's store: sorting must not query them again.
        Assert.Equal(2, vm.Messages.Count);
    }

    [Fact]
    public async Task UnreadSearchKeepsReadRowUntilExplicitSearchAndDoesNotAdmitArrivals()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory));
        store.Initialize();
        var account = new AccountModel { Id = Guid.NewGuid(), IsActive = true, BackendKind = BackendKind.LocalArchive };
        var first = Summary(account.Id, "In", "1", "<first@example.com>");
        await store.UpsertSummariesAsync([first]);
        var sync = new StubSyncService();
        using var vm = new MainViewModel(new StubImapMailService(), new StubAccountService(),
            new StubCredentialService(), store, new StubOAuthService(), sync,
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(), uiDispatcher: new StubUiDispatcher());
        vm.LoadAccountList([account]);
        vm.SelectedFolder = new MailFolderModel { AccountId = account.Id, FullName = "In", DisplayName = "In" };
        await vm.RunQuickSearchAsync("N", true);
        var row = Assert.Single(vm.Messages);
        first.IsRead = true;
        await store.UpdateIsReadBatchAsync([(account.Id, "In", "1")], true);
        sync.RaiseReadStates([first]);
        Assert.Same(row, Assert.Single(vm.Messages));
        Assert.True(row.IsRead);
        var second = Summary(account.Id, "In", "2", "<second@example.com>");
        await store.UpsertSummariesAsync([second]);
        sync.RaiseFolderSynced([first, second]);
        vm.ActiveSort = MessageSort.DateAscending;
        Assert.Same(row, Assert.Single(vm.Messages));
        var collection = vm.Messages;
        await vm.RefreshAfterPop3ReceiveAsync(account, new Pop3ReceiveResult(1, 0, 1, Skipped: false));
        Assert.Same(collection, vm.Messages);
        Assert.Same(row, Assert.Single(vm.Messages));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("2", Assert.Single(vm.Messages).MessageId);
    }

    [Fact]
    public async Task RecoveryDeleteKeepsDateSearchAndItsSurvivingRows()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory));
        store.Initialize();
        var account = new AccountModel { Id = Guid.NewGuid(), IsActive = true, BackendKind = BackendKind.LocalArchive };
        await store.EnsureLocalSystemFoldersAsync([account]);
        await store.UpsertSummariesAsync([
            Summary(account.Id, "In", "1", "<one@example.com>"),
            Summary(account.Id, "In", "2", "<two@example.com>"),
        ]);
        using var vm = new MainViewModel(new LocalMailService(store), new StubAccountService(),
            new StubCredentialService(), store, new StubOAuthService(), new StubSyncService(),
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(), uiDispatcher: new StubUiDispatcher());
        vm.LoadAccountList([account]);
        vm.SelectedFolder = new MailFolderModel { AccountId = account.Id, FullName = "In", DisplayName = "In" };
        await vm.RunQuickSearchAsync("d>30/6/2026", false);
        var collection = vm.Messages;
        var survivor = vm.Messages[1];
        await vm.DeleteMessagesAsync([vm.Messages[0]], toRecoveryDeleted: true);
        Assert.Same(collection, vm.Messages);
        Assert.Same(survivor, Assert.Single(vm.Messages));
        Assert.Same(survivor, vm.SelectedMessage);
        Assert.Equal("d>30/6/2026", vm.SearchText);
        Assert.Contains("moved to RecoveryDeleted", vm.StatusText);
    }

    [Fact]
    public void InboxArrivalsRespectAscendingDateOrderAndPreserveSelectedRow()
    {
        var account = new AccountModel { Id = Guid.NewGuid(), IsActive = true, BackendKind = BackendKind.LocalArchive };
        var sync = new StubSyncService();
        using var vm = new MainViewModel(new StubImapMailService(), new StubAccountService(),
            new StubCredentialService(), new StubLocalStoreService(), new StubOAuthService(), sync,
            new StubConfigService(), new StubCommandRegistry(), new StubViewService(),
            new StubRuleService(), new StubSmtpService(), uiDispatcher: new StubUiDispatcher());
        vm.LoadAccountList([account]);
        vm.SelectedFolder = new MailFolderModel { AccountId = account.Id, FullName = "In", DisplayName = "In" };
        vm.ActiveSort = MessageSort.DateAscending;
        var first = Summary(account.Id, "In", "1", "<one@example.com>");
        first.Date = DateTimeOffset.UtcNow.AddDays(-1);
        sync.RaiseFolderSynced([first]);
        vm.SelectedMessage = first;
        var collection = vm.Messages;
        var second = Summary(account.Id, "In", "2", "<two@example.com>");
        sync.RaiseFolderSynced([second]);
        Assert.Same(collection, vm.Messages);
        Assert.Equal(new[] { "1", "2" }, vm.Messages.Select(m => m.MessageId));
        Assert.Same(first, vm.SelectedMessage);
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
