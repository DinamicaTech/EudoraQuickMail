using QuickMail.Models;

namespace QuickMail.ViewModels;

public sealed class SearchResultsTabViewModel : TabSessionViewModel
{
    public TabSessionViewModel Origin { get; }
    public SearchResultsTabViewModel(TabSessionViewModel origin)
        : base(new TabSessionModel { Kind = TabKind.SearchResults, Title = "Search results", CanClose = true })
        => Origin = origin;
}
