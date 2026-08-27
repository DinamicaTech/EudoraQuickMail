namespace QuickMail.Helpers;

/// <summary>Normalizes the bounded, most-recent-first history used by quick search.</summary>
public static class QuickSearchHistory
{
    public const int MaximumItems = 10;

    public static List<string> Normalize(IEnumerable<string>? searches)
    {
        if (searches is null) return [];
        return searches
            .Where(search => !string.IsNullOrWhiteSpace(search))
            .Select(search => search.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumItems)
            .ToList();
    }

    public static List<string> Add(IEnumerable<string>? searches, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return Normalize(searches);
        return Normalize(new[] { search.Trim() }.Concat(searches ?? []));
    }
}
