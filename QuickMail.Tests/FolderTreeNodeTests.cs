using System.Collections.Generic;
using System.ComponentModel;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for <see cref="FolderTreeNode"/>'s in-place unread-count refresh (issue #227): the folder
/// tree must reflect a new count without a rebuild, so keyboard focus in the tree is preserved.
/// </summary>
public class FolderTreeNodeTests
{
    private static FolderTreeNode NodeFor(int unread, int total = 10) => new()
    {
        Folder = new MailFolderModel { FullName = "INBOX", DisplayName = "Inbox", UnreadCount = unread, MessageCount = total },
        Label  = "Inbox",
    };

    [Fact]
    public void CountDisplays_ReflectUnderlyingUnreadCount()
    {
        var node = NodeFor(3);
        Assert.Equal("3 unread of 10 messages", node.ItemStatusLabel);
        Assert.Equal("(3/10)", node.UnreadDisplay);
        // The count must be in the accessible Name — screen readers don't reliably announce it
        // from ItemStatus alone (issue #227).
        Assert.Equal("Inbox, 3 unread of 10 messages", node.AutomationName);
    }

    [Fact]
    public void ZeroUnread_StillDisplaysTotalMessages()
    {
        var node = NodeFor(0);
        Assert.Equal("0 unread of 10 messages", node.ItemStatusLabel);
        Assert.Equal("(0/10)", node.UnreadDisplay);
        Assert.Equal("Inbox, 0 unread of 10 messages", node.AutomationName);
    }

    [Fact]
    public void NotifyUnreadChanged_RaisesPropertyChangedForCountDisplaysAndName()
    {
        var node = NodeFor(5);
        var changed = new List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Simulate the in-place update: mutate the model, then notify.
        node.Folder!.UnreadCount = 4;
        node.NotifyUnreadChanged();

        // The accessible Name must refresh too — it carries the count for screen readers (#227).
        Assert.Contains(nameof(FolderTreeNode.AutomationName), changed);
        Assert.Contains(nameof(FolderTreeNode.ItemStatusLabel), changed);
        Assert.Contains(nameof(FolderTreeNode.UnreadDisplay), changed);
        // All reflect the new count without any tree rebuild.
        Assert.Equal("Inbox, 4 unread of 10 messages", node.AutomationName);
        Assert.Equal("4 unread of 10 messages", node.ItemStatusLabel);
        Assert.Equal("(4/10)", node.UnreadDisplay);
    }

    [Fact]
    public void LargeCounts_AreDisplayedCompactly()
    {
        var node = NodeFor(253, 4_000);
        Assert.Equal("(253/4K)", node.UnreadDisplay);
    }

    [Fact]
    public void GmailVirtualFolder_SuppressesUnreadCountEverywhere()
    {
        // All Mail / Important / Starred report counts that overlap the Inbox and include archived
        // mail, so the count is hidden in the tree and left out of the accessible name (#227).
        var node = new FolderTreeNode
        {
            Folder = new MailFolderModel
            {
                FullName = "[Gmail]/All Mail", DisplayName = "All Mail",
                UnreadCount = 23, Kind = SpecialFolderKind.AllMail,
            },
            Label = "All Mail",
        };

        Assert.Equal("", node.ItemStatusLabel);
        Assert.Equal("", node.UnreadDisplay);
        Assert.Equal("All Mail", node.AutomationName);
    }

    [Fact]
    public void NotifyUnreadChanged_DoesNotRaiseForExpansion()
    {
        var node = NodeFor(2);
        var changed = new List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        node.NotifyUnreadChanged();

        Assert.DoesNotContain(nameof(FolderTreeNode.IsExpanded), changed);
    }
}
