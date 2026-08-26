using System.IO;
using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class MessageListLayoutStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(),
        "QuickMail-layout-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Layouts_AreIndependentPerResolution()
    {
        var store = new MessageListLayoutStore(new ProfileContext(_folder));
        store.Save("1920x1080", new MessageListDisplayLayout
        {
            ReadingPaneHeight = 420,
            Columns = [new MessageListColumnLayout { Id = "subject", Width = 510 }],
        });

        var stored = store.Load("1920x1080");
        Assert.NotNull(stored);
        Assert.Equal(420, stored.ReadingPaneHeight);
        Assert.Equal("subject", Assert.Single(stored.Columns).Id);
        Assert.Null(store.Load("2560x1440"));
    }

    [Fact]
    public void Save_PreservesOtherResolutionProfiles()
    {
        var store = new MessageListLayoutStore(new ProfileContext(_folder));
        store.Save("1920x1080", new MessageListDisplayLayout { ReadingPaneHeight = 300 });
        store.Save("3840x2160", new MessageListDisplayLayout { ReadingPaneHeight = 700 });

        Assert.Equal(300, store.Load("1920x1080")?.ReadingPaneHeight);
        Assert.Equal(700, store.Load("3840x2160")?.ReadingPaneHeight);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
