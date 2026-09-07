namespace QuickMail.Models;

public enum ImapBodyCacheStatus
{
    Pending = 0,
    Downloaded = 1,
    NoTextBody = 2,
    Error = 3,
}

public sealed record ImapBodyDownloadCandidate(
    Guid AccountId,
    string FolderName,
    string MessageId,
    string InternetMessageId);

public sealed record ImapBodyBackfillProgress(long Completed, long Total, int Errors);
