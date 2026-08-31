using System.IO;
using System.Windows;
using QuickMail.Helpers;

namespace QuickMail.Tests;

public class AttachmentFileDragDropTests
{
    [StaFact]
    public void CreateDataObject_PublishesTheRealFileAsFileDrop()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quickmail-drag-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "attachment");
        try
        {
            var data = AttachmentFileDragDrop.CreateDataObject(path);

            Assert.True(data.GetDataPresent(DataFormats.FileDrop));
            var files = Assert.IsType<string[]>(data.GetData(DataFormats.FileDrop));
            Assert.Equal(new[] { Path.GetFullPath(path) }, files);
            Assert.False(data.GetDataPresent(DataFormats.UnicodeText));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [StaFact]
    public void CreateDataObject_MissingFile_IsRejectedBeforeStartingShellDrag()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.txt");
        Assert.Throws<FileNotFoundException>(() => AttachmentFileDragDrop.CreateDataObject(path));
    }
}
