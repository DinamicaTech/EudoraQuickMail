using System.IO;
using System.Text.RegularExpressions;
using QuickMail.Models;

namespace QuickMail.Services;

public static class OutgoingAttachmentValidator
{
    public static void ValidateAndLoad(ComposeModel message)
    {
        var missing = new List<string>();
        foreach (var attachment in message.Attachments)
        {
            // A complete retained copy is sufficient; the original source may have been removed.
            if (attachment.Content != null) continue;
            try
            {
                if (!string.IsNullOrEmpty(attachment.PartSpecifier) && Path.IsPathFullyQualified(attachment.PartSpecifier))
                    attachment.Content = File.ReadAllBytes(attachment.PartSpecifier);
                else missing.Add(attachment.FileName + " (not downloaded)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                missing.Add($"{attachment.FileName}: {attachment.PartSpecifier} ({ex.Message})");
            }
        }
        foreach (Match match in Regex.Matches(message.HtmlBody ?? "", "cid:([^\\s\\\"'<>]+)", RegexOptions.IgnoreCase))
        {
            var cid = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
            if (!message.Attachments.Any(a => a.IsInline && string.Equals(a.ContentId?.Trim('<', '>'), cid, StringComparison.Ordinal)))
                missing.Add("Embedded image: " + cid);
        }
        if (missing.Count > 0)
            throw new FileNotFoundException("Cannot send: these attachments or embedded images are unavailable. Restore, reattach or remove them:\n" + string.Join("\n", missing.Distinct()));
    }
}
