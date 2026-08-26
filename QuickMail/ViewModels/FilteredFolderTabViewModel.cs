using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// A single reusable background tab which points at the destination of the latest successful
/// move rule. It reuses the main message grid when activated; no second 2,000-row collection is
/// retained and applying a rule never steals focus from the user's current folder.
/// </summary>
public sealed class FilteredFolderTabViewModel : TabSessionViewModel
{
    public MailFolderModel Folder { get; private set; }
    public MailMessageSummary? LatestMovedMessage { get; private set; }

    public FilteredFolderTabViewModel(MailFolderModel folder, MailMessageSummary? movedMessage)
        : base(new TabSessionModel
        {
            Kind = TabKind.FilteredFolder,
            Title = $"Filtered → {folder.DisplayName}",
            Tooltip = "Latest filter destination",
            CanClose = true,
            ContentKey = folder.FullName,
        })
    {
        Folder = folder;
        LatestMovedMessage = movedMessage;
    }

    public void Update(MailFolderModel folder, MailMessageSummary? movedMessage)
    {
        Folder = folder;
        LatestMovedMessage = movedMessage;
        Title = $"Filtered → {folder.DisplayName}";
        Model.Tooltip = $"Latest filter destination: {folder.DisplayName}";
    }
}
