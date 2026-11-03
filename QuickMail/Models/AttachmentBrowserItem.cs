using System.IO;

namespace QuickMail.Models;

public sealed record AttachmentBrowserItem(MailMessageSummary Message, AttachmentModel Attachment)
{
    public string Name => Attachment.FileName;
    public string Extension => Path.GetExtension(Name);
    public long Size => Attachment.FileSize;
    public double SizeMb => Size / 1_048_576.0;
    public DateTimeOffset Date => Message.Date;
    public string Sender => Message.From;
    public string Subject => Message.Subject;
    public string Folder => Message.FolderName;
    public string SourcePath => Attachment.PartSpecifier ?? "";
    public override string ToString() => $"{Name}, {Subject}, {Sender}";
}

public sealed record AttachmentRecoveryCandidate(string Path, long Size, string Evidence)
{
    public double SizeMb => Size / 1_048_576.0;
    public override string ToString() => $"{Path}, {Evidence}";
}
