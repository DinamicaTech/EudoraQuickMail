using QuickMail.Models;
using QuickMail.Helpers;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class MainViewModel
{
    private sealed record SearchTabState(MailFolderModel? Folder, AccountModel? Account,
        BatchObservableCollection<MailMessageSummary> Rows, List<MailMessageSummary> Raw,
        ListState Presentation, string Query, bool Everywhere, bool Materialized, bool SearchVisible,
        IReadOnlyList<AdvancedSearchCriterion>? Advanced, SavedView? View, int Offset, long Total,
        MailMessageSummary? Selection, string Status, bool HomePendingUnread);
    private bool _homePendingUnread;
    private readonly Dictionary<TabSessionViewModel, SearchTabState> _searchTabStates = new();
    private TabSessionViewModel? _displayedListTab;
    private TabSessionViewModel? _restoredListTab;
    public bool HasRestoredListTab => ReferenceEquals(_restoredListTab, ActiveTab) && ActiveTab != null;
    public event Action<TabSessionViewModel>? ListTabStateSaving;

    private SearchTabState CaptureSearchTabState() => new(SelectedFolder, SelectedAccount, Messages,
        _rawMessages.ToList(), CurrentListState, SearchText, _searchEverywhere, _hasMaterializedSearch,
        IsSearchActive, _advancedSearchCriteria, ActiveView, LocalPageOffset, LocalTotalMessages,
        SelectedMessage, StatusText, _homePendingUnread);

    partial void OnActiveTabChanging(TabSessionViewModel? value)
    {
        if (value is HomeTabViewModel && ActiveTab is MessageListTabViewModel or SearchResultsTabViewModel or FilteredFolderTabViewModel)
        {
            _displayedListTab = ActiveTab;
            _searchTabStates[ActiveTab] = CaptureSearchTabState();
        }
        if (_searchTabStates.Count == 0) return;
        if (_displayedListTab != null)
        {
            ListTabStateSaving?.Invoke(_displayedListTab);
            var metadata = Messages.GroupBy(MessageDeduplicator.PerFolderKeyFor)
                .ToDictionary(g => g.Key, g => g.Last());
            foreach (var state in _searchTabStates.Values)
                foreach (var row in state.Rows)
                    if (metadata.TryGetValue(MessageDeduplicator.PerFolderKeyFor(row), out var fresh))
                        ReconcileMessageState(row, fresh);
            _searchTabStates[_displayedListTab] = CaptureSearchTabState();
        }
    }

    partial void OnActiveTabChanged(TabSessionViewModel? value)
    {
        OnPropertyChanged(nameof(IsMessagesView));
        if (value is HomeTabViewModel) { SelectedMessage = null; IsMessageOpen = false; }
        _restoredListTab = null;
        _displayedListTab = null;
        if (_searchTabStates.Count == 0) return;
        try { _localSearchCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _folderCts?.Cancel(); } catch (ObjectDisposedException) { }
        Interlocked.Increment(ref _folderLoadVersion);
        if (value == null || !_searchTabStates.TryGetValue(value, out var state)) return;
        _suppressFilterRebuild = true;
        try
        {
            SelectedFolder = state.Folder; SelectedAccount = state.Account; ActiveView = state.View;
            SearchText = state.Query; _searchEverywhere = state.Everywhere;
            _advancedSearchCriteria = state.Advanced; _hasMaterializedSearch = state.Materialized;
            IsSearchActive = state.SearchVisible;
            _homePendingUnread = state.HomePendingUnread;
            ApplyListState(state.Presentation);
            _rawMessages = state.Raw.ToList(); Messages = state.Rows;
            LocalPageOffset = state.Offset; LocalTotalMessages = state.Total;
            SelectedMessage = state.Selection != null && Messages.Contains(state.Selection) ? state.Selection : Messages.FirstOrDefault();
            StatusText = state.Status; MessageDetail = null; IsMessageOpen = false; IsBusy = false;
        }
        finally { _suppressFilterRebuild = false; }
        _displayedListTab = value; _restoredListTab = value;
    }

    private void PrepareSearchResultsTab(string title, bool force = false)
    {
        if (!force && !_configService.Load().OpenSearchesInResultsTab) return;
        if (ActiveTab is SearchResultsTabViewModel current) { current.Title = title; return; }
        EnsureMessageListTab();
        var origin = ActiveTab is MessageListTabViewModel or FilteredFolderTabViewModel
            ? ActiveTab : OpenTabs.OfType<MessageListTabViewModel>().First();
        var state = CaptureSearchTabState();
        _displayedListTab = origin;
        _searchTabStates[origin] = state;
        if (origin is MessageListTabViewModel main) main.Folder = SelectedFolder;
        var tab = new SearchResultsTabViewModel(origin) { Title = title };
        // Never show source rows as search results while the first query is still running.
        _searchTabStates[tab] = state with { Rows = new(), Raw = state.Raw.ToList(), Total = 0,
            Materialized = false, Selection = null, SearchVisible = true, Status = "Searching messages…" };
        tab.CloseRequested += CloseTab;
        OpenTabs.Add(tab);
        ActiveTab = tab;
        OnPropertyChanged(nameof(ShowTabStrip));
    }

    public async Task RunQuickSearchInTabAsync(string query, bool everywhere)
    {
        _homePendingUnread = false;
        if (!string.IsNullOrWhiteSpace(query))
            PrepareSearchResultsTab($"Search: {query} ({(everywhere ? "Everywhere" : SelectedFolder?.DisplayName ?? "current scope")})");
        await RunQuickSearchAsync(query, everywhere);
    }

    public async Task SearchFromHomeAsync(string query)
    {
        ActiveTab = OpenTabs.OfType<MessageListTabViewModel>().FirstOrDefault();
        PrepareSearchResultsTab($"Search: {query} (Everywhere)", force: true);
        _homePendingUnread = false;
        await RunQuickSearchAsync(query, true);
    }

    public async Task SearchUnreadFromHomeAsync()
    {
        ActiveTab = OpenTabs.OfType<MessageListTabViewModel>().FirstOrDefault();
        PrepareSearchResultsTab("Unread — excluding Trash, Junk and RecoveryDeleted (Everywhere)", force: true);
        _homePendingUnread = true;
        await RunQuickSearchAsync("N", true);
    }

    public async Task OpenHomeFolderAsync(Guid accountId, string fullName, bool unread)
    {
        var folder = _cachedFolders.GetValueOrDefault(accountId)?.FirstOrDefault(f => f.FullName == fullName)
            ?? new MailFolderModel { AccountId = accountId, FullName = fullName, DisplayName = fullName };
        await SelectFolderAsync(folder);
        if (unread) await RunQuickSearchAsync("N", false);
    }

    public async Task OpenHomeScheduledAsync(Guid? accountId)
    {
        var folder = _cachedFolders.Where(p => !accountId.HasValue || p.Key == accountId)
            .SelectMany(p => p.Value).FirstOrDefault(f => f.Kind == SpecialFolderKind.Scheduled);
        if (folder == null) throw new InvalidOperationException("Scheduled folder is unavailable. Open the account's folder tree to review the queue.");
        await SelectFolderAsync(folder);
    }

    public async Task RunAdvancedSearchInTabAsync(IReadOnlyList<AdvancedSearchCriterion> criteria)
    {
        if (criteria.Count > 0) PrepareSearchResultsTab("Advanced search results");
        await ApplyAdvancedSearchAsync(criteria);
    }

    public bool ReturnFromSearchResults()
    {
        if (ActiveTab is not SearchResultsTabViewModel tab) return false;
        ActiveTab = OpenTabs.Contains(tab.Origin) ? tab.Origin : OpenTabs.OfType<MessageListTabViewModel>().FirstOrDefault();
        return true;
    }

    private void RemoveFromInactiveListTabs(HashSet<(string, Guid, string)> keys)
    {
        foreach (var pair in _searchTabStates.ToArray())
        {
            if (pair.Key == _displayedListTab) continue;
            var state = pair.Value;
            var removed = state.Rows.Where(m => keys.Contains((m.MessageId, m.AccountId, m.FolderName))).ToList();
            foreach (var row in removed) state.Rows.Remove(row);
            state.Raw.RemoveAll(m => keys.Contains((m.MessageId, m.AccountId, m.FolderName)));
            _searchTabStates[pair.Key] = state with { Total = Math.Max(0, state.Total - removed.Count) };
        }
    }

    private void UpdateInactiveListTabs(IReadOnlyList<MailMessageSummary> incoming)
    {
        var fresh = incoming.GroupBy(MessageDeduplicator.PerFolderKeyFor)
            .ToDictionary(g => g.Key, g => g.Last());
        foreach (var pair in _searchTabStates.ToArray())
        {
            if (pair.Key == _displayedListTab) continue;
            var state = pair.Value;
            var known = state.Raw.Select(MessageDeduplicator.PerFolderKeyFor).ToHashSet();
            foreach (var row in state.Raw)
                if (fresh.TryGetValue(MessageDeduplicator.PerFolderKeyFor(row), out var updated))
                {
                    row.IsRead = updated.IsRead;
                    row.IsReplied = updated.IsReplied;
                    row.IsForwarded = updated.IsForwarded;
                }
            // Search results are snapshots. Only ordinary folder tabs accept new arrivals.
            if (pair.Key is SearchResultsTabViewModel || state.Materialized || state.View != null ||
                !string.IsNullOrEmpty(state.Query) || state.Presentation.Filter != MessageFilter.All ||
                state.Presentation.DayLimit.HasValue || state.Folder is not { } folder) continue;
            IEnumerable<MailMessageSummary> relevant;
            if (folder.FullName == AllMailFolder.FullName)
                relevant = incoming.Where(m => !IsSharedAccountId(m.AccountId));
            else if (IsFolderScopedAggregate(folder.FullName))
            {
                var sources = FolderScopedAggregateSources(folder.FullName)
                    .Select(s => (s.Account.Id, s.Folder.FullName.ToUpperInvariant())).ToHashSet();
                relevant = incoming.Where(m => sources.Contains((m.AccountId, m.FolderName.ToUpperInvariant())));
            }
            else if (TryParseRootMail(folder.FullName, out _))
            {
                var accounts = RootAccountIdsFor(folder)?.ToHashSet() ?? [];
                relevant = incoming.Where(m => accounts.Contains(m.AccountId));
            }
            else if (TryGetAccountIdFromSentinel(folder.FullName, out var account))
                relevant = incoming.Where(m => m.AccountId == account);
            else
                relevant = incoming.Where(m => m.AccountId == folder.AccountId &&
                    string.Equals(m.FolderName, folder.FullName, StringComparison.OrdinalIgnoreCase));
            var added = relevant.Where(m => !known.Contains(MessageDeduplicator.PerFolderKeyFor(m))).ToList();
            if (added.Count == 0) continue;
            var raw = state.Raw.Concat(added).ToList();
            if (IsVirtualFolder(folder)) raw = MessageDeduplicator.CollapseForAggregate(raw, ResolveFolderKind).ToList();
            _searchTabStates[pair.Key] = state with
            {
                Raw = raw,
                Rows = new(SortMessageSequence(raw, state.Presentation.Sort).Take(LocalMailConstants.MaxRenderedMessages)),
                Total = state.Total + raw.Count - state.Raw.Count
            };
        }
    }
}
