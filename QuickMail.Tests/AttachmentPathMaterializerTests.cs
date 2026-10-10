using System.IO;
using QuickMail.Helpers;
using QuickMail.Models;

namespace QuickMail.Tests;

public sealed class AttachmentPathMaterializerTests
{
    [Theory]
    [InlineData(@"C:\MissingBackupSource\Legacy\Factura.pdf")]
    [InlineData(@"\\offline-nas\archive\Legacy\Factura.pdf")]
    public async Task CopyPreservesOriginalPathWithoutAccessingFileOrMailService(string path)
    {
        var attachment = new AttachmentModel { FileName = "Factura.pdf", PartSpecifier = path };
        // No service or message is required to copy a stored path, including an offline UNC path.
        Assert.Equal(path, await AttachmentPathMaterializer.GetPathForCopyAsync(attachment, null!, null!));
        Assert.Null(attachment.Content);
    }

    [Fact]
    public async Task CopyDoesNotReplaceMissingOriginalWithTemporaryLoadedCopy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-original-{Guid.NewGuid():N}.pdf");
        var attachment = new AttachmentModel { PartSpecifier = path, Content = new byte[] { 1, 2 } };
        Assert.Equal(path, await AttachmentPathMaterializer.GetPathForCopyAsync(attachment, null!, null!));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2.1")]
    public async Task ServerOrComposeAttachmentStillMaterializesLoadedContent(string? part)
    {
        var attachment = new AttachmentModel
        {
            FileName = "Factura.pdf", PartSpecifier = part, Content = new byte[] { 1, 2, 3 }
        };
        var path = await AttachmentPathMaterializer.GetPathForCopyAsync(attachment, null!, null!);
        try
        {
            Assert.True(Path.IsPathFullyQualified(path));
            Assert.Equal(attachment.Content, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
    }
}
