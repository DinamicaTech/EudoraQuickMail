using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Installs the small, curated set of smart folders that should be useful in every profile.
/// The version marker makes this a one-time migration: users remain free to rename or delete
/// any of the views after they have been offered.
/// </summary>
internal static class BuiltInSavedViewMigration
{
    internal const int CurrentVersion = 1;

    private static readonly (Guid Id, string Name, string Query)[] Definitions =
    [
        (Guid.Parse("A43EC0D4-313D-4CF8-B54B-79B083DD9B80"), "Sent today", "D:today;O"),
        (Guid.Parse("3D95A99A-6F93-4FC6-8067-C95AB86C384A"), "Received today", "D:today;I"),
        (Guid.Parse("B5DE311C-FB5A-4A0A-994B-22397C28F5FB"), "Sent yesterday", "D:yesterday;O"),
        (Guid.Parse("3757CB5B-5C2E-4B2B-BEB0-F44C5260BB37"), "Received yesterday", "D:yesterday;I"),
        (Guid.Parse("705D5AF0-4F7D-4EB5-9297-B2541CE3A4F1"), "Flagged messages", "F"),
        (Guid.Parse("EF724061-9C0B-48BB-88B7-68D0DA70F4CE"), "Unread messages", "N"),
    ];

    internal static int Apply(IViewService viewService, IConfigService configService, ConfigModel config)
    {
        if (config.BuiltInSavedViewsVersion >= CurrentVersion) return 0;

        var views = viewService.Load();
        var added = 0;
        foreach (var definition in Definitions)
        {
            // Query + global scope is the identity that matters. This also recognises an equivalent
            // view the user created before the built-in set existed, regardless of its chosen name.
            if (views.Any(view => view.SearchEverywhere &&
                                  string.Equals(view.SearchQuery?.Trim(), definition.Query,
                                      StringComparison.OrdinalIgnoreCase)))
                continue;

            views.Add(new SavedView
            {
                Id = definition.Id,
                Name = definition.Name,
                SearchQuery = definition.Query,
                SearchEverywhere = true,
            });
            added++;
        }

        if (added > 0) viewService.Save(views);

        // Persist after views.json. If the process stops between the two writes, the next launch
        // safely retries and the query-based duplicate check prevents duplicate views.
        config.BuiltInSavedViewsVersion = CurrentVersion;
        configService.Save(config);
        return added;
    }
}
