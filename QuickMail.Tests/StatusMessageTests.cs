using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class StatusMessageTests
{
    [Theory]
    [InlineData("Warning! Link domain does not match the sender: https://example.test")]
    [InlineData("Mail check completed with 2 error(s).")]
    [InlineData("Send scheduled email (SMTP) failed for Account.")]
    [InlineData("Could not download mail: connection timed out")]
    [InlineData("No password stored for Account.")]
    [InlineData("Account is not connected.")]
    public void ImportantFailure_IsErrorStatus(string text) =>
        Assert.True(MainViewModel.IsErrorStatusMessage(text));

    [Theory]
    [InlineData("Ready")]
    [InlineData("Mail check complete.")]
    [InlineData("URL copied: https://example.test")]
    [InlineData("Downloading 2 of 10 messages…")]
    public void NormalProgress_IsNotErrorStatus(string text) =>
        Assert.False(MainViewModel.IsErrorStatusMessage(text));
}
