using QuickMail.Helpers;
using QuickMail.Models;
using Xunit;

namespace QuickMail.Tests;

public class MessageListContinuationPolicyTests
{
    private static MailMessageSummary Message(int number) => new()
    {
        AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        FolderName = "In",
        MessageId = $"message-{number}",
    };

    [Fact]
    public void RemovedMiddleRow_LandsOnNextOriginalMessage()
    {
        var original = Enumerable.Range(0, 5).Select(Message).ToList();
        var continuation = MessageListContinuationPolicy.Capture(original, [original[2]], 2);
        var refreshed = original.Where(message => message != original[2]).ToList();

        Assert.Equal(2, MessageListContinuationPolicy.Resolve(continuation, refreshed));
        Assert.Equal("message-3", refreshed[2].MessageId);
    }

    [Fact]
    public void RemovedLastRow_LandsOnNewLastRow()
    {
        var original = Enumerable.Range(0, 4).Select(Message).ToList();
        var continuation = MessageListContinuationPolicy.Capture(original, [original[3]], 3);
        var refreshed = original.Take(3).ToList();

        Assert.Equal(2, MessageListContinuationPolicy.Resolve(continuation, refreshed));
    }

    [Fact]
    public void SurvivingSelectedRow_RemainsSelectedForNonMoveRule()
    {
        var original = Enumerable.Range(0, 4).Select(Message).ToList();
        var continuation = MessageListContinuationPolicy.Capture(original, [original[1]], 1);

        Assert.Equal(1, MessageListContinuationPolicy.Resolve(continuation, original));
    }

    [Fact]
    public void BulkFilter_UsesFirstSurvivingRowAfterOriginalPosition()
    {
        var original = Enumerable.Range(0, 7).Select(Message).ToList();
        var removed = new[] { original[1], original[3], original[4] };
        var continuation = MessageListContinuationPolicy.Capture(original, removed, 3);
        var refreshed = original.Except(removed).ToList();

        var index = MessageListContinuationPolicy.Resolve(continuation, refreshed);
        Assert.Equal("message-2", refreshed[index].MessageId);
    }
}
