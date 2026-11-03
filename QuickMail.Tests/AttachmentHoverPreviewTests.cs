using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;

namespace QuickMail.Tests;

public sealed class AttachmentPreviewContentTests
{
    [Fact]
    public void LargePreviewUsesRightSideAndMovesUpToStayOnScreen()
    {
        var area = new Rect(-400, -800, 2560, 1400);
        var bounds = AttachmentHoverPreview.CalculateBounds(new Size(110, 24), area);
        Assert.Equal(122, bounds.Left);
        Assert.Equal(900, bounds.Width);
        Assert.Equal(1000, bounds.Height);
        Assert.True(area.Contains(bounds));
        Assert.True(bounds.Top < 0);
    }

    [Theory]
    [InlineData(-1100, -300, 1280, 720)]
    [InlineData(-250, -500, 1024, 768)]
    [InlineData(-200, -300, 800, 600)]
    public void PreviewFitsSmallerAndOffsetMonitorWorkAreas(double x, double y, double width, double height)
    {
        var area = new Rect(x, y, width, height);
        var bounds = AttachmentHoverPreview.CalculateBounds(new Size(100, 24), area);
        Assert.True(area.Contains(bounds));
        Assert.True(bounds.Width > 0);
        Assert.True(bounds.Height > 0);
    }
    [Theory]
    [InlineData("picture.JPG", "image")]
    [InlineData("picture.gif", "image")]
    [InlineData("document.PDF", "pdf")]
    [InlineData("notes.TXT", "text")]
    [InlineData("script.html", "unsupported")]
    [InlineData("image.svg", "unsupported")]
    public void FormatsAreExplicitlyAllowlisted(string name, string kind) => Assert.Equal(kind, AttachmentPreviewContent.Kind(name));

    [Fact]
    public void TextPreservesLinesAndBomAndIsBounded()
    {
        const string text = "Primera línea\r\nSegunda línea\n<script>literal</script>";
        Assert.Equal(text, AttachmentPreviewContent.Text(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(text, AttachmentPreviewContent.Text(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray()));
        var result = AttachmentPreviewContent.Text(Encoding.UTF8.GetBytes(new string('x', AttachmentPreviewContent.TextCharacters + 50)));
        Assert.Contains("Preview truncated", result);
        Assert.True(result.Length < AttachmentPreviewContent.TextCharacters + 150);
    }

    [Fact]
    public async Task RemoteAttachmentsAreNotAutomaticallyDownloadedAndLimitsApplyToActualBytes()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => AttachmentPreviewContent.ReadAsync(new() { PartSpecifier = "2" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AttachmentPreviewContent.ReadAsync(new() { Content = new byte[AttachmentPreviewContent.MaxBytes + 1] }, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AttachmentPreviewContent.ReadAsync(new() { Content = [1] }, new CancellationToken(true)));
    }

    [Fact]
    public async Task LocalReadClosesFileAndMissingFileDoesNotFallBackToNetwork()
    {
        var path = Path.Combine(Path.GetTempPath(), "EQM-preview-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, "hello\nworld");
            var bytes = await AttachmentPreviewContent.ReadAsync(new() { PartSpecifier = path }, CancellationToken.None);
            Assert.Equal("hello\nworld", AttachmentPreviewContent.Text(bytes));
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        finally { File.Delete(path); }
        await Assert.ThrowsAsync<FileNotFoundException>(() => AttachmentPreviewContent.ReadAsync(new() { PartSpecifier = path }, CancellationToken.None));
    }

    [Fact]
    public void ConfigurationDefaultsEnabledAndRoundTrips()
    {
        var config = new StubConfigService();
        var vm = new SettingsViewModel(config, new StubCommandRegistry());
        Assert.True(vm.AttachmentHoverPreview);
        vm.AttachmentHoverPreview = false;
        vm.SaveCommand.Execute(null);
        Assert.False(new SettingsViewModel(config, new StubCommandRegistry()).AttachmentHoverPreview);
    }
}

[Collection("WpfTests")]
public sealed class AttachmentHoverPreviewTests : WpfTestBase
{
    [WpfFact]
    public void AnchorIsTheLabelRatherThanTheStretchedAttachmentRow()
    {
        var label = new TextBlock { Name = "AttachmentPreviewAnchor", Text = "invoice.pdf" };
        var row = new ListBoxItem { Width = 1800, Content = new Border { Child = label } };
        row.Measure(new Size(1800, 100)); row.Arrange(new Rect(0, 0, 1800, 100)); row.UpdateLayout();
        Assert.Same(label, AttachmentHoverPreview.FindAnchor(row));
        Assert.True(label.DesiredSize.Width < 200);
    }
    [WpfFact]
    public async Task ClosingDuringLoadPreventsLateContentAndClicksRemainUnhandled()
    {
        var owner = new Window();
        var list = new ListBox(); owner.Content = list;
        var preview = new AttachmentHoverPreview(owner, list, () => null, new StubImapMailService(), () => true);
        try
        {
            var method = typeof(AttachmentHoverPreview).GetMethod("LoadAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var load = (Task)method.Invoke(preview, [new AttachmentModel { FileName = "test.pdf", Content = MakePdf() }, false])!;
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseDownEvent };
            list.RaiseEvent(click);
            Assert.False(click.Handled);
            await load;
            var body = (Grid)typeof(AttachmentHoverPreview).GetField("_body", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!;
            Assert.Empty(body.Children.Cast<UIElement>());
            Assert.True((bool)typeof(AttachmentHoverPreview).GetField("_suppressed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!);
        }
        finally { owner.Close(); }
    }

    [WpfFact]
    public async Task ImageIsDecodedOffThreadAndFrozenWithBoundedDimensions()
    {
        var bitmap = BitmapSource.Create(2400, 100, 96, 96, PixelFormats.Bgr24, null, new byte[2400 * 100 * 3], 2400 * 3);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output);
        var result = await Task.Run(() => AttachmentPreviewContent.Image(output.ToArray()));
        Assert.True(result.IsFrozen);
        Assert.Equal(1200, result.PixelWidth);
        Assert.Equal(50, result.PixelHeight);
    }

    [WpfFact]
    public async Task WindowsPdfRendererCreatesPagePreviewAndNavigationWithoutBrowser()
    {
        var owner = new Window();
        var list = new ListBox(); owner.Content = list;
        var preview = new AttachmentHoverPreview(owner, list, () => null, new StubImapMailService(), () => true);
        try
        {
            var method = typeof(AttachmentHoverPreview).GetMethod("RenderPdfAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)method.Invoke(preview, [MakePdf(), 0u, CancellationToken.None])!;
            var body = (Grid)typeof(AttachmentHoverPreview).GetField("_body", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!;
            var scroll = Assert.IsType<ScrollViewer>(body.Children[0]);
            Assert.NotNull(Assert.IsType<Image>(scroll.Content).Source);
            var buttons = Assert.IsType<StackPanel>(body.Children[1]);
            Assert.False(Assert.IsType<Button>(buttons.Children[0]).IsEnabled);
            Assert.True(Assert.IsType<Button>(buttons.Children[1]).IsEnabled);
            await (Task)method.Invoke(preview, [MakePdf(), 1u, CancellationToken.None])!;
            buttons = Assert.IsType<StackPanel>(body.Children[1]);
            Assert.True(Assert.IsType<Button>(buttons.Children[0]).IsEnabled);
            Assert.False(Assert.IsType<Button>(buttons.Children[1]).IsEnabled);
            var border = (Border)typeof(AttachmentHoverPreview).GetField("_border", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!;
            border.Height = 900;
            border.Measure(new Size(900, 900)); border.Arrange(new Rect(0, 0, 900, 900)); border.UpdateLayout();
            Assert.InRange(buttons.TranslatePoint(new Point(0, buttons.ActualHeight), border).Y, 885, 888);
            await (Task)method.Invoke(preview, [MakePdf(true), 0u, CancellationToken.None])!;
            Assert.Single(body.Children.Cast<UIElement>());
        }
        finally { owner.Close(); }
    }

    [WpfFact]
    public async Task ShortImageShrinksWindowAndTallImageKeepsHeightLimit()
    {
        var owner = new Window();
        var preview = new AttachmentHoverPreview(owner, new ListBox(), () => null, new StubImapMailService(), () => true);
        try
        {
            foreach (var height in new[] { 100, 2000 })
            {
                var bitmap = BitmapSource.Create(1000, height, 96, 96, PixelFormats.Bgr24, null, new byte[1000 * height * 3], 3000);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = new MemoryStream(); encoder.Save(output);
                await (Task)typeof(AttachmentHoverPreview).GetMethod("LoadAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(preview, [new AttachmentModel { FileName = "picture.png", Content = output.ToArray() }, false])!;
                var border = (Border)typeof(AttachmentHoverPreview).GetField("_border", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!;
                if (height == 100) Assert.InRange(border.Height, 100, 160);
                else Assert.Equal(880, border.Height);
            }
        }
        finally { owner.Close(); }
    }

    [WpfFact]
    public void PreviewMenuTargetsOriginalListAndHoveredAttachment()
    {
        var owner = new Window();
        var attachment = new AttachmentModel { FileName = "a.pdf" };
        var list = new ListBox { ContextMenu = new ContextMenu() };
        list.Items.Add(attachment);
        var preview = new AttachmentHoverPreview(owner, list, () => null, new StubImapMailService(), () => true);
        try
        {
            typeof(AttachmentHoverPreview).GetField("_candidate", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(preview, attachment);
            typeof(AttachmentHoverPreview).GetMethod("OpenAttachmentMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(preview, null);
            Assert.Same(list, list.ContextMenu.PlacementTarget);
            Assert.Same(attachment, list.SelectedItem);
        }
        finally { list.ContextMenu.IsOpen = false; owner.Close(); }
    }

    private static byte[] MakePdf(bool singlePage = false)
    {
        var text = new StringBuilder("%PDF-1.4\n");
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << >> >>"];
        var offsets = new List<int>();
        if (singlePage) objects[1] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
        for (var i = 0; i < objects.Length; i++) { offsets.Add(text.Length); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = text.Length;
        text.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
