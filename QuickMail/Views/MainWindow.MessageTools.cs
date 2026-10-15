using System.Windows;
using Microsoft.Win32;
using QuickMail.Services;

namespace QuickMail.Views;

public partial class MainWindow
{
    private void MenuAttachmentExplorer_Click(object sender, RoutedEventArgs e) => ShowAttachmentExplorer();
    private void ShowAttachmentExplorer(QuickMail.Models.MailMessageSummary? message = null, string? search = null)
    {
        var vm = _vm.CreateAttachmentBrowser(message);
        vm.Search = search ?? "";
        var window = new AttachmentBrowserWindow(vm) { Owner = this };
        window.GoToMessageRequested += async source =>
        {
            var target = await _vm.GoToMessageAsync(source);
            if (target == null) { vm.Status = "The message could not be located. Refresh the catalogue."; return; }
            Activate(); MessageList.SelectedItem = target; MessageList.ScrollIntoView(target); MessageList.Focus();
        };
        window.Show();
    }

    private void AttachmentContextMenu_Locate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: QuickMail.Models.AttachmentModel attachment } && _vm.MessageDetail is { } message)
            ShowAttachmentExplorer(message, attachment.FileName);
    }

    private async void MessageContextMenu_AnalyzeRules_Click(object sender, RoutedEventArgs e)
    {
        var messages = GetSelectedMessages().ToList();
        if (messages.Count == 0) { _vm.StatusText = "Select messages to analyze their matching rules."; return; }
        using var cancellation = new CancellationTokenSource();
        void Cancel(object? s, EventArgs args) => cancellation.Cancel();
        Closed += Cancel;
        try
        {
            string report = "";
            await RunWithDelayedIndicatorAsync("Analyzing matching rules…", async () => report = await _vm.AnalyzeRuleConflictsAsync(messages,cancellation.Token));
            if (!cancellation.IsCancellationRequested)
                new MessageActivityWindow("Rule conflict analysis", report,"Rule conflict analysis") { Owner = this }.Show();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { MessageBox.Show(this,ex.Message,"Rule conflict analysis",MessageBoxButton.OK,MessageBoxImage.Error); }
        finally { Closed -= Cancel; }
    }

    private async void MessageContextMenu_Activity_Click(object sender, RoutedEventArgs e) => await ShowMessageActivityAsync();
    private async Task ShowMessageActivityAsync()
    {
        var selected = GetSelectedMessages().FirstOrDefault();
        if (selected == null) return;
        try
        {
            string text = "";
            await RunWithDelayedIndicatorAsync("Loading message activity…", async () => text = await _vm.GetMessageActivityReportAsync(selected));
            new MessageActivityWindow(selected.Subject, text) { Owner = this }.Show();
        }
        catch (Exception ex)
        { MessageBox.Show(this, ex.Message, "Message activity", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void MessageContextMenu_ExportBundle_Click(object sender, RoutedEventArgs e) => await ExportBundleAsync();
    private async Task ExportBundleAsync()
    {
        var selected = GetSelectedMessages().ToList();
        if (selected.Count == 0) return;
        var dialog = new OpenFolderDialog { Title = $"Export {selected.Count:N0} selected messages with attachments and index", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        using var cancellation = new CancellationTokenSource();
        void CancelOnClose(object? sender, EventArgs e) => cancellation.Cancel();
        Closed += CancelOnClose;
        try
        {
            await RunWithDelayedIndicatorAsync("Exporting conversation…", async () =>
            {
                var result = await _vm.ExportConversationAsync(selected, dialog.FolderName, cancellation.Token);
                _vm.StatusText = $"Exported {result.Exported:N0}; incomplete {result.Failed:N0}. Index: {result.Directory}\\index.html";
                if (result.Failed > 0 && !cancellation.IsCancellationRequested)
                    MessageBox.Show(this, "Some messages could not be completely exported. The index lists the failures.\n\n" + result.Directory,
                        "Export incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { Closed -= CancelOnClose; }
    }
}
