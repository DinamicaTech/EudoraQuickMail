using System;
using System.Linq;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tab management on <see cref="MainViewModel"/>, deferred from PR #38 (issue #40).
///
/// The permanent navigation tabs are present in every message-open mode. The mode controls how
/// an individual message opens; it does not remove the message-list navigation surface:
///
/// <list type="bullet">
/// <item><b>All modes</b> — a permanent <see cref="MessageListTabViewModel"/> follows the optional
/// Calendar tab. Message tabs never move ahead of those navigation tabs, and closing the last
/// message tab falls back to the list.</item>
/// <item><b>Tab mode</b> opens selected messages as tabs by default; reading-pane and window modes
/// can still create an explicit message tab, and it follows the same navigation-tab invariants.</item>
/// </list>
///
/// Tests exercise both modes so a future mode-specific change cannot break those shared invariants.
/// </summary>
public class MainViewModelTabTests
{
    private static MailMessageSummary Msg(string id, string subject = "subject", Guid? accountId = null) => new()
    {
        MessageId  = id,
        AccountId  = accountId ?? Guid.Empty,
        FolderName = "INBOX",
        Subject    = subject,
    };

    private static MainViewModel MakeVm(MessageOpenMode mode)
    {
        var config = new StubConfigService();
        var model = config.Load();
        model.Windowing.MessageOpenMode = mode;
        config.Save(model); // MainViewModel reads Windowing.MessageOpenMode in its constructor.

        return new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), config,
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());
    }

    private static MainViewModel TabMode()        => MakeVm(MessageOpenMode.Tab);
    private static MainViewModel ReadingPaneMode() => MakeVm(MessageOpenMode.ReadingPane);

    private static int MessageTabCount(MainViewModel vm) => vm.OpenTabs.OfType<MessageTabViewModel>().Count();
    private static MessageTabViewModel[] MessageTabs(MainViewModel vm) =>
        vm.OpenTabs.OfType<MessageTabViewModel>().ToArray();

    // ── Construction ─────────────────────────────────────────────────────────────

    [Fact]
    public void TabMode_SeedsThePermanentMessageListTabAtIndexZero()
    {
        var vm = TabMode();

        Assert.Single(vm.OpenTabs);
        Assert.IsType<MessageListTabViewModel>(vm.OpenTabs[0]);
        Assert.Same(vm.OpenTabs[0], vm.ActiveTab);
        Assert.True(vm.ShowTabStrip);
    }

    [Fact]
    public void ReadingPaneMode_StillSeedsThePermanentMessageListTab()
    {
        var vm = ReadingPaneMode();

        Assert.Single(vm.OpenTabs);
        Assert.IsType<MessageListTabViewModel>(vm.OpenTabs[0]);
        Assert.Same(vm.OpenTabs[0], vm.ActiveTab);
        Assert.True(vm.ShowTabStrip);
    }

    // ── OpenMessageTab ───────────────────────────────────────────────────────────

    [Fact]
    public void OpenMessageTab_AddsTabAndActivatesIt()
    {
        var vm = ReadingPaneMode();

        vm.OpenMessageTab(Msg("1", "hello"));

        Assert.IsType<MessageListTabViewModel>(vm.OpenTabs[0]);
        var tab = Assert.Single(MessageTabs(vm));
        Assert.Same(tab, vm.ActiveTab);
        Assert.Equal("hello", tab.Title);
        Assert.True(vm.ShowTabStrip);
    }

    [Fact]
    public void OpenMessageTab_CarriesSourceFolderAndAccount()
    {
        var vm = ReadingPaneMode();
        var accountId = Guid.NewGuid();

        vm.OpenMessageTab(Msg("1", accountId: accountId));

        var tab = Assert.Single(MessageTabs(vm));
        Assert.Equal("INBOX", tab.SourceFolderName);
        Assert.Equal(accountId, tab.AccountId);
    }

    [Fact]
    public void OpenMessageTab_SameMessageTwice_ActivatesExistingInsteadOfDuplicating()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        var first = MessageTabs(vm)[0];

        vm.OpenMessageTab(Msg("1")); // already open

        Assert.Equal(2, MessageTabCount(vm));
        Assert.Same(first, vm.ActiveTab);
    }

    [Fact]
    public void OpenMessageTab_SameIdOnDifferentAccounts_OpensSeparateTabs()
    {
        // The dedupe key is (MessageId, AccountId): IMAP UIDs are only unique within a mailbox,
        // so two accounts can legitimately both have a message with the same id.
        var vm = ReadingPaneMode();

        vm.OpenMessageTab(Msg("1", accountId: Guid.NewGuid()));
        vm.OpenMessageTab(Msg("1", accountId: Guid.NewGuid()));

        Assert.Equal(2, MessageTabCount(vm));
    }

    [Fact]
    public void OpenMessageTab_InTabMode_KeepsListTabFirst()
    {
        var vm = TabMode();

        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));

        Assert.IsType<MessageListTabViewModel>(vm.OpenTabs[0]);
        Assert.Equal(3, vm.OpenTabs.Count);
    }

    // ── CloseTab ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CloseTab_RemovesTheTab()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        var first = MessageTabs(vm)[0];

        vm.CloseTab(first);

        Assert.Single(MessageTabs(vm));
        Assert.Equal(2, vm.OpenTabs.Count); // permanent list + remaining message
        Assert.DoesNotContain(first, vm.OpenTabs);
    }

    [Fact]
    public void CloseTab_ActivatesTheTabThatTookItsPosition()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        vm.OpenMessageTab(Msg("3"));
        var messageTabs = MessageTabs(vm);
        var middle = messageTabs[1];
        var third  = messageTabs[2];
        vm.ActiveTab = middle;

        vm.CloseTab(middle);

        // Index 1 is now the tab that was third — focus stays put rather than jumping to an end.
        Assert.Same(third, vm.ActiveTab);
    }

    [Fact]
    public void CloseTab_ClosingTheLastPositionActivatesTheNewLastTab()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        var messageTabs = MessageTabs(vm);
        var first = messageTabs[0];
        var last  = messageTabs[1];
        vm.ActiveTab = last;

        vm.CloseTab(last);

        Assert.Same(first, vm.ActiveTab);
    }

    [Fact]
    public void CloseTab_InactiveTab_LeavesActiveTabAlone()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        var messageTabs = MessageTabs(vm);
        var first  = messageTabs[0];
        var active = messageTabs[1];
        vm.ActiveTab = active;

        vm.CloseTab(first);

        Assert.Same(active, vm.ActiveTab);
    }

    [Fact]
    public void CloseTab_LastMessageTabInReadingPaneMode_FallsBackToTheListTab()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));

        vm.CloseTab(Assert.Single(MessageTabs(vm)));

        var listTab = Assert.IsType<MessageListTabViewModel>(Assert.Single(vm.OpenTabs));
        Assert.Same(listTab, vm.ActiveTab);
        Assert.True(vm.ShowTabStrip);
        Assert.False(vm.IsMessageOpen);
        Assert.Null(vm.MessageDetail);
    }

    [Fact]
    public void CloseTab_LastMessageTabInTabMode_FallsBackToTheListTab()
    {
        var vm = TabMode();
        vm.OpenMessageTab(Msg("1"));

        vm.CloseTab(vm.OpenTabs[1]);

        Assert.Same(vm.OpenTabs[0], vm.ActiveTab);
        Assert.IsType<MessageListTabViewModel>(vm.ActiveTab);
        // The strip stays visible in Tab mode even with no message tabs open.
        Assert.True(vm.ShowTabStrip);
        Assert.False(vm.IsMessageOpen);
    }

    [Fact]
    public void CloseTab_MessageListTab_IsRefused()
    {
        var vm = TabMode();
        var listTab = vm.OpenTabs[0];

        vm.CloseTab(listTab);

        Assert.Contains(listTab, vm.OpenTabs);
    }

    [Fact]
    public void CloseTab_TabNotInTheStrip_IsANoOp()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        var stranger = new MessageTabViewModel(Msg("stranger"));

        vm.CloseTab(stranger);

        Assert.Single(MessageTabs(vm));
        Assert.Equal(2, vm.OpenTabs.Count);
    }

    // Note: MainViewModel.OpenMessageTab subscribes to each tab's CloseRequested, but
    // TabSessionViewModel.RequestClose() is protected and MessageTabViewModel exposes no command
    // that raises it, so that wiring cannot be driven from a test (or, currently, from the app).
    // The event contract itself is covered by TabSessionModelTests via a test subclass.

    // ── Activation ───────────────────────────────────────────────────────────────

    [Fact]
    public void ActivateNextTab_MovesForwardThroughMessageTabs()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        var messageTabs = MessageTabs(vm);
        vm.ActiveTab = messageTabs[0];

        vm.ActivateNextTab();

        Assert.Same(messageTabs[1], vm.ActiveTab);
    }

    [Fact]
    public void ActivateNextTab_WrapsFromLastToFirst()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        var messageTabs = MessageTabs(vm);
        vm.ActiveTab = messageTabs[1];

        vm.ActivateNextTab();

        Assert.Same(messageTabs[0], vm.ActiveTab);
    }

    [Fact]
    public void ActivatePrevTab_WrapsFromFirstToLast()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        var messageTabs = MessageTabs(vm);
        vm.ActiveTab = messageTabs[0];

        vm.ActivatePrevTab();

        Assert.Same(messageTabs[1], vm.ActiveTab);
    }

    [Fact]
    public void ActivateNextTab_WithNoMessageTabs_IsANoOp()
    {
        var vm = TabMode(); // only the list tab exists
        var before = vm.ActiveTab;

        vm.ActivateNextTab();

        Assert.Same(before, vm.ActiveTab);
    }

    [Fact]
    public void ActivateNextTab_FromTheListTab_LandsOnTheFirstMessageTab()
    {
        // The list tab is not a MessageTabViewModel, so cycling starts at index 0.
        var vm = TabMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        vm.ActiveTab = vm.OpenTabs[0];

        vm.ActivateNextTab();

        Assert.Same(vm.OpenTabs[1], vm.ActiveTab);
    }

    [Fact]
    public void ActivatePrevTab_FromTheListTab_LandsOnTheLastMessageTab()
    {
        var vm = TabMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        vm.ActiveTab = vm.OpenTabs[0];

        vm.ActivatePrevTab();

        Assert.Same(vm.OpenTabs[2], vm.ActiveTab);
    }

    [Fact]
    public void ActivateTabByIndex_IsOneBased()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));

        vm.ActivateTabByIndex(1);

        Assert.Same(vm.OpenTabs[0], vm.ActiveTab);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void ActivateTabByIndex_OutOfBounds_IsANoOp(int index)
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        var before = vm.ActiveTab;

        vm.ActivateTabByIndex(index);

        Assert.Same(before, vm.ActiveTab);
    }

    [Fact]
    public void ActivateLastTab_WithOnlyPermanentListTab_IsANoOp()
    {
        var vm = ReadingPaneMode();
        var listTab = Assert.IsType<MessageListTabViewModel>(Assert.Single(vm.OpenTabs));

        vm.ActivateLastTab();

        Assert.Same(listTab, vm.ActiveTab);
    }

    [Fact]
    public void ActivateLastTab_WithOneTab_ActivatesIt()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        var onlyMessage = Assert.Single(MessageTabs(vm));
        vm.ActiveTab = null;

        vm.ActivateLastTab();

        Assert.Same(onlyMessage, vm.ActiveTab);
    }

    [Fact]
    public void ActivateLastTab_WithSeveralTabs_ActivatesTheRightmost()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        vm.OpenMessageTab(Msg("3"));
        var messageTabs = MessageTabs(vm);
        vm.ActiveTab = messageTabs[0];

        vm.ActivateLastTab();

        Assert.Same(messageTabs[2], vm.ActiveTab);
    }

    [Fact]
    public void ActivateMessageListTab_InTabMode_SelectsTheListTab()
    {
        var vm = TabMode();
        vm.OpenMessageTab(Msg("1"));

        Assert.True(vm.ActivateMessageListTab());
        Assert.IsType<MessageListTabViewModel>(vm.ActiveTab);
    }

    [Fact]
    public void ActivateMessageListTab_OutsideTabMode_SelectsThePermanentListTab()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));

        Assert.True(vm.ActivateMessageListTab());
        Assert.IsType<MessageListTabViewModel>(vm.ActiveTab);
    }

    // ── Reordering ───────────────────────────────────────────────────────────────

    [Fact]
    public void MoveTabLeft_SwapsWithThePreviousTab()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1", "first"));
        vm.OpenMessageTab(Msg("2", "second"));
        var second = MessageTabs(vm)[1];
        vm.ActiveTab = second;

        vm.MoveTabLeft();

        Assert.IsType<MessageListTabViewModel>(vm.OpenTabs[0]);
        Assert.Same(second, vm.OpenTabs[1]);
        Assert.Same(second, vm.ActiveTab); // the moved tab stays active
    }

    [Fact]
    public void MoveTabLeft_AtTheLeftEdge_IsANoOp()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        var first = MessageTabs(vm)[0];
        vm.ActiveTab = first;

        vm.MoveTabLeft();

        Assert.IsType<MessageListTabViewModel>(vm.OpenTabs[0]);
        Assert.Same(first, vm.OpenTabs[1]);
    }

    [Fact]
    public void MoveTabLeft_InTabMode_WillNotDisplaceTheListTab()
    {
        // The list tab must stay at index 0, so the leftmost legal position for a message tab is 1.
        var vm = TabMode();
        vm.OpenMessageTab(Msg("1"));
        var messageTab = vm.OpenTabs[1];
        vm.ActiveTab = messageTab;

        vm.MoveTabLeft();

        Assert.IsType<MessageListTabViewModel>(vm.OpenTabs[0]);
        Assert.Same(messageTab, vm.OpenTabs[1]);
    }

    [Fact]
    public void MoveTabRight_SwapsWithTheNextTab()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        var first = MessageTabs(vm)[0];
        vm.ActiveTab = first;

        vm.MoveTabRight();

        Assert.Same(first, vm.OpenTabs[2]);
    }

    [Fact]
    public void MoveTabRight_AtTheRightEdge_IsANoOp()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        var last = MessageTabs(vm)[1];
        vm.ActiveTab = last;

        vm.MoveTabRight();

        Assert.Same(last, vm.OpenTabs[2]);
    }

    [Fact]
    public void MoveTab_WithTheListTabActive_IsANoOp()
    {
        var vm = TabMode();
        vm.OpenMessageTab(Msg("1"));
        vm.ActiveTab = vm.OpenTabs[0];

        vm.MoveTabRight();
        vm.MoveTabLeft();

        Assert.IsType<MessageListTabViewModel>(vm.OpenTabs[0]);
    }

    // ── CloseAllOtherTabs ────────────────────────────────────────────────────────

    [Fact]
    public void CloseAllOtherTabs_LeavesOnlyTheActiveTab()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        vm.OpenMessageTab(Msg("3"));
        var keep = MessageTabs(vm)[1];
        vm.ActiveTab = keep;

        vm.CloseAllOtherTabs();

        Assert.Equal(2, vm.OpenTabs.Count);
        Assert.IsType<MessageListTabViewModel>(vm.OpenTabs[0]);
        Assert.Same(keep, vm.OpenTabs[1]);
        Assert.Same(keep, vm.ActiveTab);
    }

    [Fact]
    public void CloseAllOtherTabs_InTabMode_KeepsThePermanentListTab()
    {
        var vm = TabMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        var keep = vm.OpenTabs[2];
        vm.ActiveTab = keep;

        vm.CloseAllOtherTabs();

        Assert.Equal(2, vm.OpenTabs.Count);
        Assert.IsType<MessageListTabViewModel>(vm.OpenTabs[0]);
        Assert.Same(keep, vm.OpenTabs[1]);
    }

    [Fact]
    public void CloseAllOtherTabs_WithASingleMessageTab_IsANoOp()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        var only = Assert.Single(MessageTabs(vm));

        vm.CloseAllOtherTabs();

        Assert.Equal(2, vm.OpenTabs.Count);
        Assert.IsType<MessageListTabViewModel>(vm.OpenTabs[0]);
        Assert.Same(only, vm.OpenTabs[1]);
    }

    [Fact]
    public void CloseAllOtherTabs_WithNoActiveTab_IsANoOp()
    {
        var vm = ReadingPaneMode();
        vm.OpenMessageTab(Msg("1"));
        vm.OpenMessageTab(Msg("2"));
        vm.ActiveTab = null;

        vm.CloseAllOtherTabs();

        Assert.Equal(3, vm.OpenTabs.Count);
        Assert.Equal(2, MessageTabCount(vm));
    }
}
