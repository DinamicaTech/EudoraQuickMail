using System.IO;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class BuiltInSavedViewMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "QuickMail-built-in-views-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NewProfileReceivesTheSixCuratedGlobalSearches()
    {
        var profile = new ProfileContext(_directory);
        var views = new ViewService(profile);
        var config = new ConfigService(profile);
        var model = config.Load();

        var added = BuiltInSavedViewMigration.Apply(views, config, model);

        Assert.Equal(6, added);
        var saved = views.Load();
        Assert.Equal(6, saved.Count);
        Assert.All(saved, view => Assert.True(view.SearchEverywhere));
        Assert.Equal(
            ["D:today;O", "D:today;I", "D:yesterday;O", "D:yesterday;I", "F", "N"],
            saved.Select(view => view.SearchQuery));
        Assert.Equal(BuiltInSavedViewMigration.CurrentVersion,
            config.Load().BuiltInSavedViewsVersion);
    }

    [Fact]
    public void ExistingEquivalentViewsArePreservedAndOnlyMissingOnesAreAdded()
    {
        var profile = new ProfileContext(_directory);
        var views = new ViewService(profile);
        var config = new ConfigService(profile);
        var existingId = Guid.NewGuid();
        views.Save(
        [
            new SavedView
            {
                Id = existingId,
                Name = "My received mail",
                SearchQuery = "d:today;i",
                SearchEverywhere = true,
            },
        ]);

        var added = BuiltInSavedViewMigration.Apply(views, config, config.Load());

        Assert.Equal(5, added);
        var saved = views.Load();
        Assert.Equal(6, saved.Count);
        Assert.Contains(saved, view => view.Id == existingId && view.Name == "My received mail");
    }

    [Fact]
    public void CompletedMigrationDoesNotRestoreAViewTheUserLaterDeletes()
    {
        var profile = new ProfileContext(_directory);
        var views = new ViewService(profile);
        var config = new ConfigService(profile);
        BuiltInSavedViewMigration.Apply(views, config, config.Load());
        var saved = views.Load();
        saved.RemoveAt(0);
        views.Save(saved);

        var added = BuiltInSavedViewMigration.Apply(views, config, config.Load());

        Assert.Equal(0, added);
        Assert.Equal(5, views.Load().Count);
    }

    [Fact]
    public void DisplayModeDefaultsMatchTheFirstRunExperience()
    {
        var config = new ConfigModel();

        Assert.True(config.RememberViewPerFolder);
        Assert.True(config.ShowCalendar);
        Assert.True(config.ShowTodayAgenda);
        Assert.True(config.ShowFilteredDestinationTab);
        Assert.False(config.ShowCombinedViews);
        Assert.False(config.ShowAccountsPanel);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
