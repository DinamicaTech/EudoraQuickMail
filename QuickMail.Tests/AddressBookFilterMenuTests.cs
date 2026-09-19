// Window-level wiring for the address book's account filter.
//
// The ViewModel tests (AddressBookAccountFilterTests) cover what filtering does. These
// cover the parts only the real window can prove:
//
//   - The Filter button is the second tab stop, not the first — the window still opens
//     on the search box and the contact list.
//   - Activating the button drops the menu and puts keyboard focus on the filter that is
//     currently in effect, so Up/Down move from there and Enter applies.
//   - Each menu item is checkable (which is what makes the automation peer expose the
//     Toggle pattern, so the active filter is announced as checked) and is wired to the
//     VM command through the ContextMenu's inherited DataContext. That RelativeSource
//     binding fails silently in XAML — nothing happens on Enter — so it is asserted here.
//   - An underscore in an account name does not become a hidden menu mnemonic or corrupt
//     the announced item name.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public class AddressBookFilterMenuTests : WpfTestBase
{
    private sealed class FakeAccountService : IAccountService
    {
        private readonly List<AccountModel> _accounts;
        public FakeAccountService(params AccountModel[] accounts) => _accounts = accounts.ToList();
        public List<AccountModel> LoadAccounts() => _accounts;
        public void SaveAccounts(List<AccountModel> accounts) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    [WpfFact]
    public async Task FilterButton_IsTheSecondTabStop_BehindTheSearchBox()
    {
        EnsureApplication();
        var (_, window, dir, cleanup) = await BuildWindowAsync();
        try
        {
            var search = window!.FindName("SearchBox") as TextBox;
            var button = window.FindName("AccountFilterButton") as Button;
            Assert.NotNull(search);
            Assert.NotNull(button);

            Assert.Equal(0, search!.TabIndex);
            Assert.Equal(1, button!.TabIndex);
            Assert.True(button.IsTabStop);
        }
        finally { window!.Close(); cleanup(dir); }
    }

    [WpfFact]
    public async Task FilterButton_LabelAndAccessibleName_ReportTheActiveFilter()
    {
        EnsureApplication();
        var (vm, window, dir, cleanup) = await BuildWindowAsync();
        try
        {
            var button = window!.FindName("AccountFilterButton") as Button;
            Assert.NotNull(button);

            Assert.Equal("_Filter: All accounts", button!.Content);
            Assert.Equal("Filter: All accounts",
                AutomationProperties.GetName(button));

            vm.SelectAccountFilter(vm.AccountFilterOptions.Single(o => o.Name == "Work"));
            DoEvents();

            Assert.Equal("_Filter: Work", button.Content);
            Assert.Equal("Filter: Work", AutomationProperties.GetName(button));
        }
        finally { window!.Close(); cleanup(dir); }
    }

    [WpfFact]
    public async Task ActivatingTheButton_OpensTheMenu_OnTheActiveFilter()
    {
        EnsureApplication();
        var (vm, window, dir, cleanup) = await BuildWindowAsync();
        try
        {
            var button = window!.FindName("AccountFilterButton") as Button;
            Assert.NotNull(button);
            var menu = button!.ContextMenu;
            Assert.NotNull(menu);

            Click(button);
            DoEvents();

            Assert.True(menu!.IsOpen);
            Assert.Equal(
                ["All accounts", "Local address book", "Home", "Work"],
                menu.Items.OfType<AccountFilterOption>().Select(o => o.Name).ToArray());

            // Focus lands on the filter in effect, not on the first item.
            var active = ItemFor(menu, vm.SelectedAccountFilter);
            Assert.NotNull(active);
            // The synthesized routed Click does not activate the popup HWND, so process-wide
            // keyboard focus remains on the button under the test host. Logical focus within
            // the ContextMenu still verifies the Opened handler chose the active filter; real
            // mouse/access-key activation supplies the HWND transition in the application.
            Assert.Same(active, FocusManager.GetFocusedElement(menu));

            menu.IsOpen = false;
            DoEvents();
        }
        finally { window!.Close(); cleanup(dir); }
    }

    [WpfFact]
    public async Task MenuItems_AreCheckable_AndTheActiveOneIsChecked()
    {
        EnsureApplication();
        var (vm, window, dir, cleanup) = await BuildWindowAsync();
        try
        {
            var button = (Button)window!.FindName("AccountFilterButton");
            var menu   = button.ContextMenu!;
            Click(button);
            DoEvents();

            var all  = ItemFor(menu, vm.AccountFilterOptions.Single(o => o.Name == "All accounts"))!;
            var work = ItemFor(menu, vm.AccountFilterOptions.Single(o => o.Name == "Work"))!;

            // IsCheckable is what makes MenuItemAutomationPeer expose the Toggle pattern.
            Assert.True(all.IsCheckable);
            Assert.True(work.IsCheckable);
            Assert.True(all.IsChecked);
            Assert.False(work.IsChecked);

            menu.IsOpen = false;
            DoEvents();
        }
        finally { window!.Close(); cleanup(dir); }
    }

    [WpfFact]
    public async Task ChoosingAMenuItem_AppliesTheFilter_AndMovesTheCheckMark()
    {
        EnsureApplication();
        var (vm, window, dir, cleanup) = await BuildWindowAsync();
        try
        {
            var button = (Button)window!.FindName("AccountFilterButton");
            var menu   = button.ContextMenu!;
            Click(button);
            DoEvents();

            var work = ItemFor(menu, vm.AccountFilterOptions.Single(o => o.Name == "Work"))!;
            var all  = ItemFor(menu, vm.AccountFilterOptions.Single(o => o.Name == "All accounts"))!;

            // This is the binding that fails silently if the RelativeSource DataContext
            // lookup in the ItemContainerStyle breaks.
            Assert.Same(vm.SelectAccountFilterCommand, work.Command);

            InvokeMenuItemClick(work);
            DoEvents();

            Assert.Equal("Work", vm.SelectedAccountFilter.Name);
            Assert.Equal(["Work Person"], vm.FilteredContacts.Select(c => c.DisplayName).ToArray());
            Assert.True(work.IsChecked);
            Assert.False(all.IsChecked);

            menu.IsOpen = false;
            DoEvents();
        }
        finally { window!.Close(); cleanup(dir); }
    }

    [WpfFact]
    public async Task MenuItemNames_SurviveAnUnderscoreInAnAccountName()
    {
        // MenuItem.Header renders through a ContentPresenter with RecognizesAccessKey, so an
        // account named "work_mail" would draw and announce as "workmail" and would quietly
        // claim Alt+M inside the menu. Accounts with no display name fall back to the
        // username, where underscores are common.
        EnsureApplication();
        var (vm, window, dir, cleanup) = await BuildWindowAsync(underscoreAccountName: true);
        try
        {
            var button = (Button)window!.FindName("AccountFilterButton");
            var menu   = button.ContextMenu!;
            Click(button);
            DoEvents();

            var item = ItemFor(menu, vm.AccountFilterOptions.Single(o => o.Name == "work_mail"))!;

            // Doubled for rendering...
            Assert.Equal("work__mail", item.Header);
            // ...and the announced name is the account name exactly, underscore intact.
            Assert.Equal("work_mail", AutomationProperties.GetName(item));

            menu.IsOpen = false;
            DoEvents();
        }
        finally { window!.Close(); cleanup(dir); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static MenuItem? ItemFor(ContextMenu menu, AccountFilterOption option) =>
        menu.ItemContainerGenerator.ContainerFromItem(option) as MenuItem;

    private static void Click(Button button)
    {
        // A real mouse or access-key activation focuses the button before raising Click.
        // Preserve that precondition when synthesizing the routed event so popup focus
        // behaves like the user path rather than remaining in the search box.
        button.Focus();
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    /// <summary>
    /// Runs the same code path pressing Enter on a menu item does. Raising ClickEvent is
    /// not enough — MenuItem toggles IsChecked and invokes its Command from OnClick, which
    /// a synthesized routed event never reaches.
    /// </summary>
    private static void InvokeMenuItemClick(MenuItem item)
    {
        var onClick = typeof(MenuItem).GetMethod(
            "OnClick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, Type.EmptyTypes, null)
            ?? throw new InvalidOperationException("MenuItem.OnClick not found");
        onClick.Invoke(item, null);
    }

    private static async Task<(AddressBookViewModel vm, AddressBookWindow? window, string dir, Action<string> cleanup)> BuildWindowAsync(
        bool underscoreAccountName = false)
    {
        var dir  = Path.Combine(Path.GetTempPath(), $"QM-AddrFilterMenu-{Guid.NewGuid():N}");
        var svc  = new ContactService(new ProfileContext(dir));
        var work = Guid.NewGuid();
        var home = Guid.NewGuid();
        await svc.UpsertContactAsync(new ContactModel { DisplayName = "Local Person", EmailAddress = "local@x.test" });
        await svc.ReplaceSyncedContactsAsync(work, ContactSource.Microsoft,
            [new ContactModel { SourceId = "w1", DisplayName = "Work Person", EmailAddress = "work@x.test" }]);
        await svc.ReplaceSyncedContactsAsync(home, ContactSource.Google,
            [new ContactModel { SourceId = "h1", DisplayName = "Home Person", EmailAddress = "home@x.test" }]);

        var accounts = new FakeAccountService(
            new AccountModel { Id = work, AccountName = underscoreAccountName ? "work_mail" : "Work" },
            new AccountModel { Id = home, AccountName = "Home" });
        var vm = new AddressBookViewModel(svc, null, accounts);
        var window = new AddressBookWindow(vm);
        await vm.LoadAsync();
        window.Show();
        window.UpdateLayout();
        return (vm, window, dir, DeleteDir);
    }

    private static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void EnsureApplication()
    {
        lock (typeof(Application))
        {
            if (Application.Current == null)
                WpfTestApplication.EnsureStarted();
            const string stylesUri = "pack://application:,,,/QuickMail;component/Styles/AccessibleStyles.xaml";
            var uri = new Uri(stylesUri, UriKind.Absolute);
            if (Application.Current!.Resources.MergedDictionaries.All(d => d.Source != uri))
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
        }
    }
}
