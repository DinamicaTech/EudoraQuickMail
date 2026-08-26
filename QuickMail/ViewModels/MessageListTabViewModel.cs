using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// Permanent sentinel tab representing the message list in Tab mode.
/// Always first in the tab strip; cannot be closed by the user.
/// When active, the reading pane is hidden and the message list is shown.
/// </summary>
public sealed class MessageListTabViewModel : TabSessionViewModel
{
    /// <summary>The folder to restore when returning from a secondary navigation tab.</summary>
    public MailFolderModel? Folder { get; set; }

    public MessageListTabViewModel()
        : base(new TabSessionModel
        {
            Kind     = TabKind.MessageList,
            Title    = "Messages",
            Tooltip  = "Message list",
            CanClose = false,
        })
    {
        CanClose = false;
    }

    public override bool CanCloseNow() => false;
}
