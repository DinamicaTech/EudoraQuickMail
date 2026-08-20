using QuickMail.Models;

namespace QuickMail.ViewModels;

public sealed class ComposeTabViewModel : TabSessionViewModel
{
    public object Content { get; }
    public Func<Task<bool>> TryCloseAsync { get; }
    public Action ForceDispose { get; }
    public Action AddAttachments { get; }
    public Func<Guid, string, string, bool> IsEditingDraft { get; }
    public Action DiscardDeletedDraft { get; }

    public ComposeTabViewModel(string title, object content, Func<Task<bool>> tryCloseAsync,
        Action forceDispose, Action addAttachments,
        Func<Guid, string, string, bool> isEditingDraft, Action discardDeletedDraft)
        : base(new TabSessionModel { Kind = TabKind.Compose, Title = title, CanClose = true })
    {
        Content = content;
        TryCloseAsync = tryCloseAsync;
        ForceDispose = forceDispose;
        AddAttachments = addAttachments;
        IsEditingDraft = isEditingDraft;
        DiscardDeletedDraft = discardDeletedDraft;
    }
}
