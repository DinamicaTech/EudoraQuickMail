using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using System.IO;

namespace QuickMail.Tests;

public sealed class SearchResultsTabTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QuickMailSearchTabs-" + Guid.NewGuid().ToString("N"));
    private readonly StubConfigService _config = new();
    private readonly StubSyncService _sync = new();
    private readonly LocalStoreService _store;
    private readonly MainViewModel _vm;
    private readonly AccountModel _account = new() { Id = Guid.NewGuid(), IsActive = true, BackendKind = BackendKind.LocalArchive };

    public SearchResultsTabTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new LocalStoreService(new ProfileContext(_directory));
        _store.Initialize();
        _vm = new MainViewModel(new StubImapMailService(), new StubAccountService(),
            new StubCredentialService(), _store, new StubOAuthService(), _sync,
            _config, new StubCommandRegistry(), new StubViewService(), new StubRuleService(),
            new StubSmtpService(), uiDispatcher: new StubUiDispatcher());
        _vm.LoadAccountList([_account]);
        _vm.SelectedFolder = new MailFolderModel { AccountId = _account.Id, FullName = "In", DisplayName = "In" };
        _vm.EnsureMessageListTab();
        _vm.ActiveTab = _vm.OpenTabs.OfType<MessageListTabViewModel>().Single();
    }

    private MailMessageSummary Message(string id, string folder = "In") => new()
    {
        MessageId = id, AccountId = _account.Id, FolderName = folder,
        Subject = "needle " + id, Date = DateTimeOffset.Now, InternetMessageId = $"<{id}@example.com>"
    };

    [Fact]
    public async Task SearchRestoresOriginalRowsSelectionAndScopeAndReusesResultsTab()
    {
        var row = Message("1");
        await _store.UpsertSummariesAsync([row, Message("2", "Out")]);
        var original = new BatchObservableCollection<MailMessageSummary>([row]);
        _vm.Messages = original;
        _vm.SelectedMessage = row;
        var origin = _vm.ActiveTab;
        await _vm.RunQuickSearchInTabAsync("needle", true);
        var search = Assert.IsType<SearchResultsTabViewModel>(_vm.ActiveTab);
        Assert.Equal(2, _vm.Messages.Count);
        Assert.NotSame(original, _vm.Messages);
        await _vm.RunQuickSearchInTabAsync("needle", false);
        Assert.Same(search, _vm.ActiveTab);
        Assert.Single(_vm.Messages);
        Assert.True(_vm.ReturnFromSearchResults());
        Assert.Same(origin, _vm.ActiveTab);
        Assert.Same(original, _vm.Messages);
        Assert.Same(row, _vm.SelectedMessage);
        Assert.Equal(string.Empty, _vm.SearchText);
        Assert.False(_vm.SearchEverywhere);
        _vm.ActiveTab = search;
        Assert.Equal("needle", _vm.SearchText);
        Assert.Single(_vm.Messages);
    }

    [Fact]
    public async Task DisabledSettingKeepsExistingInlineSearch()
    {
        _config.Load().OpenSearchesInResultsTab = false;
        await _store.UpsertSummariesAsync([Message("1")]);
        var origin = _vm.ActiveTab;
        await _vm.RunQuickSearchInTabAsync("needle", true);
        Assert.Same(origin, _vm.ActiveTab);
        Assert.DoesNotContain(_vm.OpenTabs, t => t is SearchResultsTabViewModel);
        Assert.Single(_vm.Messages);
    }

    [Fact]
    public async Task NewArrivalsEnterInactiveInboxButNotFrozenSearch()
    {
        await _store.UpsertSummariesAsync([Message("1")]);
        _sync.RaiseFolderSynced([Message("1")]);
        await _vm.RunQuickSearchInTabAsync("needle", true);
        var search = _vm.ActiveTab;
        _sync.RaiseFolderSynced([Message("2")]);
        Assert.Single(_vm.Messages);
        _vm.ReturnFromSearchResults();
        Assert.Equal(2, _vm.Messages.Count);
        _vm.ActiveTab = search;
        Assert.Single(_vm.Messages);
    }

    [Fact]
    public async Task ClosingResultsReturnsToOriginAndSecondSearchCreatesIndependentTab()
    {
        await _store.UpsertSummariesAsync([Message("1")]);
        var origin = _vm.ActiveTab;
        await _vm.RunQuickSearchInTabAsync("needle", false);
        var first = _vm.ActiveTab!;
        _vm.ReturnFromSearchResults();
        await _vm.RunQuickSearchInTabAsync("absent", true);
        var second = _vm.ActiveTab!;
        Assert.NotSame(first, second);
        Assert.Empty(_vm.Messages);
        _vm.CloseTab(second);
        Assert.Same(origin, _vm.ActiveTab);
        _vm.ActiveTab = first;
        Assert.Single(_vm.Messages);
        Assert.False(_vm.SearchEverywhere);
    }

    [Fact]
    public void SettingDefaultsOnAndPersistsOff()
    {
        var config = new ConfigService(new ProfileContext(_directory));
        Assert.True(config.Load().OpenSearchesInResultsTab);
        var model = config.Load();
        model.OpenSearchesInResultsTab = false;
        config.Save(model);
        Assert.False(new ConfigService(new ProfileContext(_directory)).Load().OpenSearchesInResultsTab);
    }

    [Fact]
    public async Task RemovedRowsDisappearFromBothTabsWithoutRerunningSearch()
    {
        var rows = new[] { Message("1"), Message("2") };
        await _store.UpsertSummariesAsync(rows);
        _sync.RaiseFolderSynced(rows);
        await _vm.RunQuickSearchInTabAsync("needle", true);
        var search = _vm.ActiveTab;
        _sync.RaiseMessagesRemoved([rows[0]]);
        Assert.Equal("2", Assert.Single(_vm.Messages).MessageId);
        _vm.ReturnFromSearchResults();
        Assert.Equal("2", Assert.Single(_vm.Messages).MessageId);
        _vm.ActiveTab = search;
        Assert.Equal("2", Assert.Single(_vm.Messages).MessageId);
    }

    [Fact]
    public async Task ChangingFolderDoesNotReplaceExistingResultsAndReadMetadataIsShared()
    {
        var row = Message("1");
        await _store.UpsertSummariesAsync([row]);
        _sync.RaiseFolderSynced([row]);
        await _vm.RunQuickSearchInTabAsync("needle", true);
        var search = _vm.ActiveTab;
        _vm.Messages[0].IsRead = true;
        _vm.ReturnFromSearchResults();
        Assert.True(Assert.Single(_vm.Messages).IsRead);
        await _vm.SelectFolderCommand.ExecuteAsync(new MailFolderModel
            { AccountId = _account.Id, FullName = "Out", DisplayName = "Out" });
        Assert.Equal("Out", _vm.SelectedFolder?.FullName);
        _vm.ActiveTab = search;
        Assert.Equal("In", _vm.SelectedFolder?.FullName);
        Assert.Equal("needle", _vm.SearchText);
        Assert.Single(_vm.Messages);
    }

    [Fact]
    public async Task ExplicitRefreshAdmitsNewMatchesWithoutReplacingTheTab()
    {
        await _store.UpsertSummariesAsync([Message("1")]);
        await _vm.RunQuickSearchInTabAsync("needle", true);
        var tab = _vm.ActiveTab;
        await _store.UpsertSummariesAsync([Message("2")]);
        Assert.Single(_vm.Messages);
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Same(tab, _vm.ActiveTab);
        Assert.Equal(2, _vm.Messages.Count);
    }

    [Fact]
    public async Task ResultsSortDoesNotChangeSourcePresentation()
    {
        await _store.UpsertSummariesAsync([Message("1"), Message("2")]);
        var originalSort = _vm.ActiveSort;
        await _vm.RunQuickSearchInTabAsync("needle", true);
        var tab = _vm.ActiveTab;
        _vm.ActiveSort = MessageSort.AlphaAscending;
        _vm.ReturnFromSearchResults();
        Assert.Equal(originalSort, _vm.ActiveSort);
        _vm.ActiveTab = tab;
        Assert.Equal(MessageSort.AlphaAscending, _vm.ActiveSort);
    }

    public void Dispose()
    {
        _vm.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
