using QuickMail.Models;

namespace QuickMail.ViewModels;

public sealed class ComposeTabViewModel : TabSessionViewModel
{
    public object Content { get; }
    public Func<Task<bool>> TryCloseAsync { get; }
    public Action ForceDispose { get; }

    public ComposeTabViewModel(string title, object content, Func<Task<bool>> tryCloseAsync, Action forceDispose)
        : base(new TabSessionModel { Kind = TabKind.Compose, Title = title, CanClose = true })
    {
        Content = content;
        TryCloseAsync = tryCloseAsync;
        ForceDispose = forceDispose;
    }
}
