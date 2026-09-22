using System.IO;
using QuickMail.Views;

namespace QuickMail.Tests;

public class ComposeHtmlAttachmentDropTests
{
    [Fact]
    public void HugeRteEditor_ForwardsDroppedFilesToQuickMailInsteadOfBlockingThem()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "QuickMail", "Assets", "HugeRte", "editor.html"));

        Assert.Contains("block_unsupported_drop: false", source, StringComparison.Ordinal);
        Assert.Contains("paste_block_drop: false", source, StringComparison.Ordinal);
        Assert.Contains("type: 'attachment-drop'", source, StringComparison.Ordinal);
        Assert.Contains("getDoc().addEventListener('drop', handleAttachmentDrop, true)", source,
            StringComparison.Ordinal);
        Assert.Contains("event.stopImmediatePropagation()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("getBody().addEventListener('drop', handleAttachmentDrop)", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Executable_EmbedsTheCurrentAttachmentDropBridge()
    {
        using var stream = ComposeWindow.OpenBundledHtmlEditorBridge();
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();

        Assert.Contains("block_unsupported_drop: false", source, StringComparison.Ordinal);
        Assert.Contains("type: 'attachment-drop'", source, StringComparison.Ordinal);
        Assert.Contains("getDoc().addEventListener('drop', handleAttachmentDrop, true)", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WebViewInstallsAnEarlyFileDropBridgeBeforeHugeRteLoads()
    {
        var script = ComposeWindow.EarlyAttachmentDropBridgeScript;

        Assert.Contains("document.addEventListener('dragover'", script, StringComparison.Ordinal);
        Assert.Contains("document.addEventListener('drop'", script, StringComparison.Ordinal);
        Assert.Contains("event.stopImmediatePropagation()", script, StringComparison.Ordinal);
        Assert.Contains("type: 'attachment-drop'", script, StringComparison.Ordinal);
        Assert.Contains("reader.readAsDataURL(file)", script, StringComparison.Ordinal);
        Assert.Contains("window.top.postMessage({ quickMailAttachmentDrop: payload }", script,
            StringComparison.Ordinal);
        Assert.Contains("window.chrome?.webview?.postMessage(payload)", script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextEditorUsesPreviewDropSoChildEditorsCannotConsumeFiles()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "QuickMail", "Views", "ComposeWindow.xaml"));

        Assert.Contains("PreviewDragOver=\"Window_DragOver\"", source, StringComparison.Ordinal);
        Assert.Contains("PreviewDrop=\"Window_Drop\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain(" DragOver=\"Window_DragOver\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain(" Drop=\"Window_Drop\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HugeRteSelectionReplacementPreservesTranslatedLineBreaks()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "QuickMail", "Assets", "HugeRte", "editor.html"));

        var functionStart = source.IndexOf("window.quickmailReplaceSelection = text =>", StringComparison.Ordinal);
        Assert.True(functionStart >= 0, "The HTML compose selection replacement bridge is missing.");
        var functionEnd = source.IndexOf("window.quickmailSetLanguage", functionStart, StringComparison.Ordinal);
        Assert.True(functionEnd > functionStart, "The HTML compose selection replacement bridge is incomplete.");
        var replacementBridge = source[functionStart..functionEnd];

        Assert.Contains("dom.encode(text).replace(/\\r\\n|\\r|\\n/g, '<br>')", replacementBridge,
            StringComparison.Ordinal);
        Assert.Contains("selection.setContent(html)", replacementBridge, StringComparison.Ordinal);
        Assert.DoesNotContain("selection.setContent(quickMailEditor.dom.encode(text))", replacementBridge,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HugeRteTemplateInsertionPreservesStoredLineBreaks()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "QuickMail", "Assets", "HugeRte", "editor.html"));

        var functionStart = source.IndexOf("window.quickmailInsertText = text =>", StringComparison.Ordinal);
        Assert.True(functionStart >= 0, "The HTML compose template insertion bridge is missing.");
        var functionEnd = source.IndexOf("window.quickmailGetSelection", functionStart, StringComparison.Ordinal);
        Assert.True(functionEnd > functionStart, "The HTML compose template insertion bridge is incomplete.");
        var insertionBridge = source[functionStart..functionEnd];

        Assert.Contains("dom.encode(text).replace(/\\r\\n|\\r|\\n/g, '<br>')", insertionBridge,
            StringComparison.Ordinal);
        Assert.Contains("insertContent(html)", insertionBridge, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "QuickMail", "Assets")))
                return directory.FullName;
        }

        throw new InvalidOperationException($"Repo source tree not found from {AppContext.BaseDirectory}.");
    }
}
