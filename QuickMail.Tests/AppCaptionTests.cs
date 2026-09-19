using QuickMail.Helpers;
using QuickMail.ViewModels;

namespace QuickMail.Tests;

public class AppCaptionTests
{
    [Fact]
    public void MainCaptionIncludesThePublishedAssemblyVersion()
    {
        Assert.Equal($"Eudora QuickMail v{AppVersion.Display}", MainViewModel.ProductCaption);
    }
}
