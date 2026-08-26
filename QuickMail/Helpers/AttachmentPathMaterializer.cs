using System.IO;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>Resolves a stored attachment to a real local path for clipboard and Explorer actions.</summary>
public static class AttachmentPathMaterializer
{
    public static async Task<string> EnsureLocalPathAsync(AttachmentModel attachment,
        MailMessageDetail detail, IMailService mailService, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(attachment.PartSpecifier) &&
            Path.IsPathFullyQualified(attachment.PartSpecifier) && File.Exists(attachment.PartSpecifier))
            return Path.GetFullPath(attachment.PartSpecifier);

        if (!attachment.IsLoaded)
        {
            if (string.IsNullOrWhiteSpace(attachment.PartSpecifier))
                throw new FileNotFoundException("The attachment has no local or server reference.");
            attachment.Content = await mailService.DownloadAttachmentAsync(
                detail.AccountId, detail.FolderName, detail.MessageId, attachment.PartSpecifier, ct);
        }

        var directory = Path.Combine(Path.GetTempPath(), "QuickMail", "Attachments",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, AttachmentSafety.SanitizeFileName(attachment.FileName));
        await File.WriteAllBytesAsync(path, attachment.Content!, ct);
        return path;
    }
}
