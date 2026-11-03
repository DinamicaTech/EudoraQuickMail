using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using QuickMail.Models;

namespace QuickMail.Helpers;

internal static class AttachmentPreviewContent
{
    internal const int MaxBytes = 25 * 1024 * 1024;
    internal const int TextCharacters = 64 * 1024;
    internal static string Kind(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" => "image",
        ".pdf" => "pdf", ".txt" => "text", _ => "unsupported"
    };

    internal static async Task<byte[]> ReadAsync(AttachmentModel attachment, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (attachment.Content is { } bytes)
        {
            if (bytes.Length > MaxBytes) throw new InvalidOperationException("Preview limit: 25 MB. Double-click to open the attachment.");
            return bytes;
        }
        var path = attachment.PartSpecifier;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("Download the attachment to preview it.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, true);
        if (stream.Length > MaxBytes) throw new InvalidOperationException("Preview limit: 25 MB. Double-click to open the attachment.");
        // Enforce the limit even if the file grows after opening it.
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > MaxBytes) throw new InvalidOperationException("Preview limit: 25 MB.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static string Text(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
        var buffer = new char[TextCharacters + 1];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, Math.Min(count, TextCharacters)) +
            (count > TextCharacters ? "\n\n[Preview truncated — open the attachment to read the rest.]" : "");
    }

    internal static BitmapImage Image(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 100_000_000)
            throw new InvalidOperationException("Image dimensions exceed the preview limit.");
        var scale = Math.Min(1.0, Math.Min(1200.0 / frame.PixelWidth, 1200.0 / frame.PixelHeight));
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * scale));
        image.DecodePixelHeight = Math.Max(1, (int)(frame.PixelHeight * scale));
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
