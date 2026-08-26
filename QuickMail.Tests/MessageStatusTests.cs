using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class MessageStatusTests
{
    [Fact]
    public void AssigningFlagNameRefreshesStatusAndReplyDoesNotReplaceIt()
    {
        var message = new MailMessageSummary { IsRead = true, IsReplied = true };
        var changes = new List<string?>();
        message.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        message.FlagId = Guid.NewGuid().ToString();
        message.FlagName = "Important";

        Assert.Equal("Important", message.StatusDisplay);
        Assert.Contains(nameof(MailMessageSummary.StatusDisplay), changes);
        message.FlagId = null;
        Assert.Equal(string.Empty, message.StatusDisplay);
        Assert.Equal("↩", message.ReplyIndicator);
    }

    [Theory]
    [InlineData("Google Alerts <alerts@gmail.com>", "alerts@gmail.com")]
    [InlineData("plain@example.com", "plain@example.com")]
    public void SenderMailboxExtractsAddress(string source, string expected) =>
        Assert.Equal(expected, MainViewModel.SenderMailbox(source));
}
