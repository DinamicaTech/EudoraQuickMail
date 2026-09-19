using QuickMail.Helpers;

namespace QuickMail.Tests;

public sealed class PermanentDeleteConfirmationPolicyTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(2500, true)]
    public void RequiresConfirmation_UsesFiveMessageThreshold(int count, bool expected)
    {
        Assert.Equal(expected, PermanentDeleteConfirmationPolicy.RequiresConfirmation(count));
    }

    [Fact]
    public void BuildPrompt_StatesCountAndIrreversibleOutcome()
    {
        var prompt = PermanentDeleteConfirmationPolicy.BuildPrompt(12);

        Assert.Contains("12 messages", prompt);
        Assert.Contains("cannot be undone", prompt);
    }
}
