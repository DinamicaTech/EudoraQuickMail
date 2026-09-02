using System.IO;

namespace QuickMail.Tests;

public class ComposeHtmlAttachmentDropTests
{
    [Fact]
    public void HugeRteEditor_ForwardsDroppedFilesToQuickMailInsteadOfBlockingThem()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "QuickMail", "Assets", "HugeRte", "editor.html"));

        Assert.Contains("block_unsupported_drop: false", source, StringComparison.Ordinal);
        Assert.Contains("type: 'attachment-drop'", source, StringComparison.Ordinal);
        Assert.Contains("addEventListener('drop', handleAttachmentDrop)", source, StringComparison.Ordinal);
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
