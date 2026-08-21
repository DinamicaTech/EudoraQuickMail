using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using QuickMail.Services;

namespace QuickMail.Views;

public partial class OrphanedAttachmentsWindow : Window
{
    private readonly OrphanAttachmentService _service;
    private readonly ObservableCollection<OrphanAttachmentItem> _items = [];
    private IReadOnlyList<string> _roots = [];

    public OrphanedAttachmentsWindow(OrphanAttachmentService service)
    {
        _service = service;
        InitializeComponent();
        AttachmentGrid.ItemsSource = _items;
        Loaded += async (_, _) => await LoadItemsAsync();
    }

    private async Task LoadItemsAsync()
    {
        SetBusy(true, "Scanning attachment folders…");
        try
        {
            var result = await Task.Run(() => _service.ScanAsync());
            _roots = result.Roots;
            _items.Clear();
            foreach (var item in result.Items.OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase))
                _items.Add(item);
            LocationText.Text = _roots.Count == 0
                ? "No attachment folder was found for the current data profile."
                : "Scanned: " + string.Join("; ", _roots);
            SetBusy(false, $"{_items.Count:N0} orphaned attachment(s), {_items.Sum(item => item.SizeBytes) / 1024:N0} KB.");
        }
        catch (Exception ex)
        {
            SetBusy(false, "Scan failed.");
            MessageBox.Show(this, ex.Message, "Delete Orphaned Attachments",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e) =>
        RemoveItems(AttachmentGrid.SelectedItems.Cast<OrphanAttachmentItem>().ToList());

    private void RemoveAll_Click(object sender, RoutedEventArgs e) => RemoveItems(_items.ToList());

    private void RemoveItems(IReadOnlyList<OrphanAttachmentItem> items)
    {
        if (items.Count == 0) return;
        var sizeKb = items.Sum(item => item.SizeBytes) / 1024;
        if (MessageBox.Show(this,
                $"Permanently delete {items.Count:N0} orphaned attachment(s) ({sizeKb:N0} KB)?\n\nThis cannot be undone.",
                "Delete Orphaned Attachments", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        foreach (var item in items)
        {
            try
            {
                _service.Remove(item, _roots);
                _items.Remove(item);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Deletion stopped at: {item.FullPath}";
                MessageBox.Show(this, $"Could not delete:\n{item.FullPath}\n\n{ex.Message}",
                    "Delete Orphaned Attachments", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        StatusText.Text = $"Removed {items.Count:N0} attachment(s). {_items.Count:N0} remain.";
    }

    private void CopyList_Click(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder("Full path\tDate/time\tSize (KB)\r\n");
        foreach (var item in AttachmentGrid.Items.Cast<OrphanAttachmentItem>())
            text.Append(item.FullPath).Append('\t').Append(item.LastWriteTime.ToString("G"))
                .Append('\t').Append(item.SizeKb.ToString("N0")).Append("\r\n");
        Clipboard.SetText(text.ToString());
        StatusText.Text = $"Copied {_items.Count:N0} row(s) to the clipboard.";
    }

    private void AttachmentGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not System.Windows.Controls.DataGridRow)
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        if (current is not System.Windows.Controls.DataGridRow) return;
        if (AttachmentGrid.SelectedItem is not OrphanAttachmentItem item) return;
        var dangerous = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".exe", ".com", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".ps1", ".scr", ".msi" };
        if (dangerous.Contains(Path.GetExtension(item.FullPath)) && MessageBox.Show(this,
                "This file type can execute code. Open it anyway?", "Potentially Unsafe Attachment",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open Attachment", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy, string status)
    {
        StatusText.Text = status;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        RemoveSelectedButton.IsEnabled = !busy;
        RemoveAllButton.IsEnabled = !busy;
        CopyButton.IsEnabled = !busy;
    }
}
