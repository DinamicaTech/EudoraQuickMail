using QuickMail.Models;

namespace QuickMail.ViewModels;

public sealed class HomeTabViewModel() : TabSessionViewModel(new TabSessionModel
{
    Kind = TabKind.Home, Title = "Home", Tooltip = "Account health, backups and mail activity", CanClose = false
})
{
    public override bool CanCloseNow() => false;
}
