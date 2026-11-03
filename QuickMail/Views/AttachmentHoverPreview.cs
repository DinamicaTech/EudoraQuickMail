using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace QuickMail.Views;

/// <summary>Non-activating, delayed preview shared by the reading pane and detached messages.</summary>
internal sealed class AttachmentHoverPreview
{
    private readonly ListBox _list;
    private readonly Window _owner;
    private readonly Func<MailMessageDetail?> _detail;
    private readonly IMailService _mail;
    private readonly Func<bool> _enabled;
    private readonly Func<MailMessageDetail, CancellationToken, Task<string>> _serverFolder;
    private readonly Popup _popup = new() { StaysOpen = true, AllowsTransparency = true, Placement = PlacementMode.Custom };
    private readonly Border _border;
    private double _maximumHeight = 880;
    private double _contentWidth = 840;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Grid _body = new();
    private ContextMenu? _previewMenu;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private AttachmentModel? _candidate;
    private MailMessageDetail? _message;
    private FrameworkElement? _anchor;
    private DateTime _entered, _left;
    private bool _suppressed, _busy;
    private CancellationTokenSource? _work;
    private byte[]? _pdfBytes;
    private uint _page, _pages;

    internal AttachmentHoverPreview(Window owner, ListBox list, Func<MailMessageDetail?> detail,
        IMailService mail, Func<bool> enabled,
        Func<MailMessageDetail, CancellationToken, Task<string>>? serverFolder = null)
    {
        _owner = owner; _list = list; _detail = detail; _mail = mail; _enabled = enabled;
        _serverFolder = serverFolder ?? ((message, _) => Task.FromResult(message.FolderName));
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        _body.RowDefinitions.Add(new RowDefinition());
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_body, 1);
        panel.Children.Add(_status); panel.Children.Add(_body);
        var border = new Border { Child = panel, Width = 900, Padding = new Thickness(12),
            BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Background = SystemColors.WindowBrush };
        border.SetValue(TextElement.ForegroundProperty, SystemColors.WindowTextBrush);
        _popup.Child = border;
        _border = border;
        _popup.CustomPopupPlacementCallback = (size, _, _) =>
        {
            var bounds = GetPreviewBounds(size.Height);
            return [new CustomPopupPlacement(new Point(bounds.X, bounds.Y), PopupPrimaryAxis.None)];
        };
        _timer.Tick += (_, _) => Tick();
        owner.Loaded += (_, _) => _timer.Start();
        if (owner.IsLoaded) _timer.Start();
        owner.Closed += (_, _) => { _timer.Stop(); Close(); };
        owner.Deactivated += (_, _) => Suppress();
        list.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => Suppress()), true);
        list.ContextMenuOpening += (_, _) => Suppress();
        list.ToolTipOpening += (_, e) => { if (_candidate != null && !_suppressed) e.Handled = true; };
        owner.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _popup.IsOpen) { Suppress(); e.Handled = true; } };
        border.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Suppress(); e.Handled = true; } };
        border.PreviewMouseRightButtonUp += (_, e) => { e.Handled = true; OpenAttachmentMenu(); };
    }

    private void OpenAttachmentMenu()
    {
        if (_candidate == null || !_list.Items.Contains(_candidate) || _list.ContextMenu == null) return;
        // Keep the original command bindings and click handlers (all target the attachment list).
        _list.SelectedItem = _candidate;
        var menu = _list.ContextMenu;
        var placement = menu.Placement;
        menu.PlacementTarget = _list;
        menu.Placement = PlacementMode.MousePoint;
        _previewMenu = menu;
        RoutedEventHandler? closed = null;
        closed = (_, _) =>
        {
            menu.Closed -= closed;
            menu.Placement = placement;
            _previewMenu = null;
            _left = DateTime.UtcNow;
        };
        menu.Closed += closed;
        menu.IsOpen = true;
    }

    private void Suppress() { Close(); _suppressed = true; }
    private void Close()
    {
        _work?.Cancel(); _work?.Dispose(); _work = null;
        if (_previewMenu != null) _previewMenu.IsOpen = false;
        _popup.IsOpen = false; _body.Children.Clear(); _pdfBytes = null; _busy = false;
    }

    private void Tick()
    {
        if (!_owner.IsActive || !_list.IsVisible) { Close(); _candidate = null; return; }
        var item = Mouse.DirectlyOver is DependencyObject source
            ? ItemsControl.ContainerFromElement(_list, source) as ListBoxItem : null;
        var attachment = item?.DataContext as AttachmentModel;
        if ((_message != null && !ReferenceEquals(_message, _detail())) ||
            (_candidate != null && !_list.Items.Contains(_candidate))) { Close(); _candidate = null; }
        if (_previewMenu?.IsOpen == true) { _left = DateTime.UtcNow; return; }
        if (_popup.IsOpen && _popup.Child.IsMouseOver) { _left = DateTime.UtcNow; return; }
        if (attachment == null)
        {
            if (DateTime.UtcNow - _left > TimeSpan.FromMilliseconds(400))
            { Close(); _candidate = null; _suppressed = false; }
            return;
        }
        _left = DateTime.UtcNow;
        if (!ReferenceEquals(_candidate, attachment))
        {
            Close(); _candidate = attachment; _anchor = FindAnchor(item!) ?? item; _message = _detail();
            _entered = DateTime.UtcNow; _suppressed = !_enabled();
        }
        if (_suppressed || _popup.IsOpen || Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed) return;
        if (DateTime.UtcNow - _entered < TimeSpan.FromMilliseconds(750)) return;
        _popup.PlacementTarget = _anchor;
        var bounds = GetPreviewBounds();
        _border.Width = bounds.Width;
        _border.Height = bounds.Height;
        _maximumHeight = bounds.Height;
        _contentWidth = Math.Max(1, bounds.Width - 60);
        _status.MaxHeight = 70;
        _status.Text = attachment.FileName + "\n" + attachment.FileSizeDisplay;
        _popup.IsOpen = true;
        if (AttachmentPreviewContent.Kind(attachment.FileName) == "unsupported")
        { _status.Text += "\nNo preview for this format. Double-click the attachment to open it."; return; }
        if (attachment.Content == null && (attachment.PartSpecifier == null || !Path.IsPathFullyQualified(attachment.PartSpecifier)))
        {
            _status.Text += "\nNot downloaded. Preview downloads only when requested (maximum 25 MB).";
            var download = new Button { Content = "Download and preview", Margin = new Thickness(0, 8, 0, 0) };
            download.Click += async (_, _) => { download.IsEnabled = false; await LoadAsync(attachment, true); };
            _body.Children.Add(download);
        }
        else _ = LoadAsync(attachment, false);
    }

    private async Task LoadAsync(AttachmentModel attachment, bool download)
    {
        if (_busy) return;
        _busy = true;
        var work = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _work = work;
        var ct = work.Token;
        var message = _message;
        _status.Text = attachment.FileName + "\nLoading…";
        try
        {
            byte[] bytes;
            if (download)
            {
                if (attachment.FileSize > AttachmentPreviewContent.MaxBytes) throw new InvalidOperationException("Preview limit: 25 MB. Double-click to open.");
                if (message == null || attachment.PartSpecifier == null) throw new InvalidOperationException("Message unavailable.");
                bytes = await _mail.DownloadAttachmentAsync(message.AccountId,
                    await _serverFolder(message, ct), message.MessageId, attachment.PartSpecifier, ct);
                if (bytes.Length > AttachmentPreviewContent.MaxBytes) throw new InvalidOperationException("Preview limit: 25 MB.");
                ct.ThrowIfCancellationRequested();
                attachment.Content = bytes;
            }
            else bytes = await Task.Run(() => AttachmentPreviewContent.ReadAsync(attachment, ct), ct);
            ct.ThrowIfCancellationRequested();
            _body.Children.Clear();
            switch (AttachmentPreviewContent.Kind(attachment.FileName))
            {
                case "text":
                    var text = await Task.Run(() => AttachmentPreviewContent.Text(bytes), ct);
                    ct.ThrowIfCancellationRequested();
                    _body.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                    break;
                case "image":
                    var image = await Task.Run(() => AttachmentPreviewContent.Image(bytes), ct);
                    ct.ThrowIfCancellationRequested();
                    _status.Text = attachment.FileName;
                    _status.Measure(new Size(Math.Max(1, _border.Width - 26), double.PositiveInfinity));
                    _border.Height = Math.Min(_maximumHeight,
                        Math.Ceiling(_contentWidth * image.PixelHeight / image.PixelWidth + _status.DesiredSize.Height + 26));
                    _body.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = new Image { Source = image, Width = _contentWidth, Stretch = Stretch.Uniform } });
                    break;
                case "pdf":
                    _pdfBytes = bytes; _page = 0;
                    await RenderPdfAsync(bytes, 0, ct);
                    break;
            }
            ct.ThrowIfCancellationRequested();
            _status.Text = attachment.FileName + (_pages > 0 && AttachmentPreviewContent.Kind(attachment.FileName) == "pdf" ? $" — {_page + 1}/{_pages}" : "");
        }
        catch (OperationCanceledException) { if (ReferenceEquals(_work, work) && _popup.IsOpen) _status.Text = "Preview cancelled or timed out."; }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested && ReferenceEquals(_work, work))
            { _status.Text = "Cannot preview: " + ex.Message + (Path.IsPathFullyQualified(attachment.PartSpecifier ?? "") ? "\nOriginal path: " + attachment.PartSpecifier : ""); }
        }
        finally { if (ReferenceEquals(_work, work)) { _busy = false; _work = null; } work.Dispose(); }
    }

    private async Task RenderPdfAsync(byte[] bytes, uint number, CancellationToken ct)
    {
        // Windows renders pages to pixels: no browser navigation, scripts or remote resources.
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input)) { writer.WriteBytes(bytes); await writer.StoreAsync().AsTask(ct); writer.DetachStream(); }
        input.Seek(0);
        var document = await PdfDocument.LoadFromStreamAsync(input).AsTask(ct);
        _pages = document.PageCount;
        using var page = document.GetPage(number);
        using var rendered = new InMemoryRandomAccessStream();
        var scale = Math.Min(900 / page.Size.Width, 1200 / page.Size.Height);
        await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
            DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale)
        }).AsTask(ct);
        rendered.Seek(0);
        using var reader = new DataReader(rendered);
        await reader.LoadAsync((uint)rendered.Size).AsTask(ct);
        var pixels = new byte[(int)rendered.Size]; reader.ReadBytes(pixels);
        var image = await Task.Run(() => AttachmentPreviewContent.Image(pixels), ct);
        ct.ThrowIfCancellationRequested();
        _body.Children.Clear();
        _body.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Image { Source = image, Stretch = Stretch.Uniform, Width = _contentWidth } });
        if (_pages <= 1) return;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetRow(buttons, 1);
        foreach (var delta in new[] { -1, 1 })
        {
            var button = new Button { Content = delta < 0 ? "Previous page" : "Next page", Margin = new Thickness(4),
                IsEnabled = delta < 0 ? number > 0 : number + 1 < _pages };
            button.Click += async (_, _) => await ChangePageAsync(delta);
            buttons.Children.Add(button);
        }
        _body.Children.Add(buttons);
    }

    private async Task ChangePageAsync(int delta)
    {
        if (_busy || _pdfBytes == null) return;
        _busy = true;
        var previousPage = _page;
        var work = new CancellationTokenSource(TimeSpan.FromSeconds(30)); _work = work;
        try
        {
            _page = (uint)((int)_page + delta);
            await RenderPdfAsync(_pdfBytes, _page, work.Token);
            _status.Text = $"{_candidate?.FileName} — {_page + 1}/{_pages}";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_work, work))
            {
                _page = previousPage;
                _status.Text = work.IsCancellationRequested ? "Preview timed out." : "Cannot preview page: " + ex.Message;
            }
        }
        finally { if (ReferenceEquals(_work, work)) { _work = null; _busy = false; } work.Dispose(); }
    }

    internal static FrameworkElement? FindAnchor(DependencyObject parent)
    {
        if (parent is FrameworkElement { Name: "AttachmentPreviewAnchor" } anchor) return anchor;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindAnchor(VisualTreeHelper.GetChild(parent, i));
            if (found != null) return found;
        }
        return null;
    }

    private Rect GetPreviewBounds(double height = 1000)
    {
        if (_anchor == null) return new Rect(0, 0, 900, 880);
        var origin = _anchor.PointToScreen(new Point());
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)origin.X, (int)origin.Y)).WorkingArea;
        var transform = PresentationSource.FromVisual(_anchor)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(screen.Left - origin.X, screen.Top - origin.Y));
        var bottomRight = transform.Transform(new Point(screen.Right - origin.X, screen.Bottom - origin.Y));
        return CalculateBounds(new Size(Math.Min(_anchor.ActualWidth, _anchor.DesiredSize.Width), _anchor.ActualHeight),
            new Rect(topLeft, bottomRight), height);
    }

    // All values are DIPs relative to the actual attachment label, not its stretched ListBox row.
    internal static Rect CalculateBounds(Size anchor, Rect workArea, double desiredHeight = 1000)
    {
        const double margin = 12;
        var available = new Rect(workArea.Left + margin, workArea.Top + margin,
            Math.Max(1, workArea.Width - margin * 2), Math.Max(1, workArea.Height - margin * 2));
        var right = anchor.Width + margin;
        var rightSpace = available.Right - right;
        var height = Math.Min(desiredHeight, available.Height);
        double width, x;
        if (rightSpace >= Math.Min(320, available.Width))
        { width = Math.Min(900, rightSpace); x = right; }
        else
        {
            var leftSpace = -margin - available.Left;
            width = Math.Min(900, Math.Max(1, leftSpace));
            x = -margin - width;
            if (leftSpace < 320)
            { width = Math.Min(900, available.Width); x = available.Left; }
        }
        var y = Math.Clamp(anchor.Height / 2 - height / 2, available.Top, available.Bottom - height);
        return new Rect(x, y, width, height);
    }
}
