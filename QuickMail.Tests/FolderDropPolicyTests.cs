using QuickMail.Models;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

public class FolderDropPolicyTests
{
    [Fact]
    public void CtrlShiftDrop_TreatsLegacyEmptyLeafAsContainer()
    {
        var folder = new MailFolderModel { IsContainer = false, MessageCount = 0 };

        Assert.True(MainWindow.IsContainerDropTarget(folder, childCount: 0, controlShiftDrop: true));
    }

    [Fact]
    public void PlainDrop_CanStillUseAnEmptyPhysicalLeaf()
    {
        var folder = new MailFolderModel { IsContainer = false, MessageCount = 0 };

        Assert.False(MainWindow.IsContainerDropTarget(folder, childCount: 0, controlShiftDrop: false));
    }

    [Fact]
    public void CtrlShiftDrop_DoesNotReclassifyNonEmptyLeaf()
    {
        var folder = new MailFolderModel { IsContainer = false, MessageCount = 1 };

        Assert.False(MainWindow.IsContainerDropTarget(folder, childCount: 0, controlShiftDrop: true));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void ExplicitOrVisibleContainer_AlwaysRequiresSubfolder(bool isContainer, int childCount)
    {
        var folder = new MailFolderModel { IsContainer = isContainer, MessageCount = 10 };

        Assert.True(MainWindow.IsContainerDropTarget(folder, childCount, controlShiftDrop: false));
    }
}
