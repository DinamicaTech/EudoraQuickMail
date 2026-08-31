using System.IO;
using System.Windows;

namespace QuickMail.Helpers;

/// <summary>
/// Builds the native shell payload used when an attachment is dragged to File Explorer.
/// Publishing only text or a URI makes Explorer create a shortcut; CF_HDROP/FileDrop makes it
/// copy the materialized file itself.
/// </summary>
public static class AttachmentFileDragDrop
{
    public static DataObject CreateDataObject(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An attachment path is required.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The materialized attachment was not found.", fullPath);

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { fullPath }, autoConvert: true);
        return data;
    }

    public static DragDropEffects Begin(UIElement source, string path) =>
        DragDrop.DoDragDrop(source, CreateDataObject(path), DragDropEffects.Copy);
}
