using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Tests;

public sealed class FolderPathNormalizerTests
{
    [Theory]
    [InlineData(" Personal / Arnau /King's InterHigh ", "Personal/Arnau/King's InterHigh")]
    [InlineData(@"Personal\ Arnau \ School ", "Personal/Arnau/School")]
    [InlineData("// Personal /// School //", "Personal/School")]
    public void Normalize_TrimsEveryPathSegment(string source, string expected) =>
        Assert.Equal(expected, FolderPathNormalizer.Normalize(source));

    [Fact]
    public void Rewrite_UpdatesExactFolderAndDescendantsOnly()
    {
        var moves = new[]
        {
            new CanonicalFolderMoveResult("Personal/Arnau ", "Personal/Arnau", false),
        };

        Assert.Equal("Personal/Arnau/School",
            FolderReferenceRewriter.Rewrite("Personal/Arnau /School", moves));
        Assert.Equal("Personal/Arnau2/School",
            FolderReferenceRewriter.Rewrite("Personal/Arnau2/School", moves));
    }
}
