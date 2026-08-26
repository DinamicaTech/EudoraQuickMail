using QuickMail.Models;

namespace QuickMail.ViewModels;

public sealed class CalendarTabViewModel : TabSessionViewModel
{
    public CalendarTabViewModel() : base(new TabSessionModel
    {
        Kind = TabKind.Calendar,
        Title = "Calendar",
        Tooltip = "Calendar",
        CanClose = false,
    }) => CanClose = false;

    public override bool CanCloseNow() => false;
}
