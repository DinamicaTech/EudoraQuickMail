namespace QuickMail.Models;

public sealed record LocalSearchQuery(
    string Text,
    Guid? AccountId = null,
    string? FolderName = null,
    int Limit = LocalMailConstants.MaxRenderedMessages,
    int Offset = 0,
    LocalSearchSort Sort = LocalSearchSort.NewestFirst,
    bool IncludeDescendants = false);

public enum LocalSearchSort
{
    NewestFirst, OldestFirst, Relevance, FromAscending, FromDescending,
    SubjectAscending, SubjectDescending, ReadAscending, ReadDescending, AttachmentsFirst, AttachmentsLast
    , ToAscending, ToDescending
}

public static class LocalMailConstants
{
    /// <summary>Empirical UI safety cap. Search still counts every match in SQLite.</summary>
    public const int MaxRenderedMessages = 2000;
}

public sealed record LocalSearchResult(IReadOnlyList<MailMessageSummary> Messages, long TotalMatches);

public sealed record AdvancedSearchCriterion(string Field, string Operator, string Value, string Join = "AND");
public sealed record AdvancedSearchQuery(IReadOnlyList<AdvancedSearchCriterion> Criteria,
    Guid? AccountId = null, string? FolderName = null, int Limit = LocalMailConstants.MaxRenderedMessages,
    int Offset = 0, LocalSearchSort Sort = LocalSearchSort.NewestFirst, bool IncludeDescendants = false);
