using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;

namespace QuickMail.Tests;

[Collection("WpfTests")]
public sealed class AttachmentBrowserPresentationTests : WpfTestBase
{
    [WpfFact]
    public async Task SlowSearchShowsScopeAndElapsedTimeAndCanBeCancelled()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EQM-splash-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        var store = new QuickMail.Services.LocalStoreService(new QuickMail.Services.ProfileContext(root));
        store.Initialize();
        var scope = new TaskCompletionSource<List<MailMessageSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = new AttachmentBrowserViewModel(store, new StubImapMailService(), () => scope.Task, "In");
        var window = new AttachmentBrowserWindow(vm);
        try
        {
            window.Show();
            await Task.Delay(150);
            Assert.Empty(window.OwnedWindows.Cast<Window>());
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!window.OwnedWindows.Cast<Window>().OfType<SlowOperationWindow>().Any() && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            var splash = Assert.Single(window.OwnedWindows.Cast<Window>().OfType<SlowOperationWindow>());
            while (((TextBlock)splash.FindName("OperationStatus")).Text == "Working…" && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            var status = ((TextBlock)splash.FindName("OperationStatus")).Text;
            Assert.Contains("Scope: In", status);
            Assert.Contains("Elapsed:", status);
            Assert.Contains("not directories", status);
            ((Button)splash.FindName("CancelOperation")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            scope.TrySetResult([]);
            deadline = DateTime.UtcNow.AddSeconds(5);
            while ((vm.IsBusy || splash.IsVisible) && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.Equal("Cancelled.", vm.Status);
            Assert.False(splash.IsVisible);
        }
        finally
        {
            scope.TrySetResult([]);
            window.Close();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(root, true);
        }
    }

    [WpfFact]
    public async Task SizeUsesRegionalTwoDecimalsNumericSortingAndTitleCountsVisibleRows()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var (culture, expected) in new[] { ("es-ES", "1.234,50"), ("en-US", "1,234.50") })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                using var vm = new AttachmentBrowserViewModel(null, new StubImapMailService(),
                    () => Task.FromResult(new List<MailMessageSummary>()), "In");
                var window = new AttachmentBrowserWindow(vm);
                try
                {
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.DataBind);
                    var item = new AttachmentBrowserItem(new(), new() { FileSize = 1_294_467_072 });
                    vm.Items.Add(item);
                    window.GetBindingExpression(Window.TitleProperty)!.UpdateTarget();
                    Assert.Equal("Attachment explorer — 1 files shown", window.Title);
                    var grid = (DataGrid)window.FindName("AttachmentsGrid");
                    var column = Assert.IsType<DataGridTextColumn>(grid.Columns.Single(c => Equals(c.Header, "MB")));
                    Assert.Equal("Size", column.SortMemberPath);
                    var text = new TextBlock { DataContext = item, Language = window.Language, Style = column.ElementStyle };
                    BindingOperations.SetBinding(text, TextBlock.TextProperty, column.Binding);
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.DataBind);
                    text.GetBindingExpression(TextBlock.TextProperty)!.UpdateTarget();
                    Assert.Equal(expected, text.Text);
                    Assert.Equal(TextAlignment.Right, text.TextAlignment);
                    vm.Items.Clear();
                    window.GetBindingExpression(Window.TitleProperty)!.UpdateTarget();
                    Assert.Equal("Attachment explorer — 0 files shown", window.Title);
                    Assert.Equal(item.SizeMb, new AttachmentRecoveryCandidate("path", item.Size, "test").SizeMb);
                }
                finally { window.Close(); }
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
