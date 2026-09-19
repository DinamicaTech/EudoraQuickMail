using QuickMail.Views;

namespace QuickMail.Tests;

public sealed class GoogleAuthorizationFailureHintTests
{
    [Theory]
    [InlineData("Error:'unauthorized_client', Description:'Unauthorized'")]
    [InlineData("invalid_client")]
    public void RejectedClientExplainsCredentialsAndRelinking(string failure)
    {
        var hint = MainWindow.BuildGoogleAuthorizationFailureHint([failure]);

        Assert.Contains("Client ID", hint);
        Assert.Contains("Client Secret", hint);
        Assert.Contains("sign in again", hint);
    }

    [Fact]
    public void RevokedGrantRequestsRelinkingWithoutBlamingClientConfiguration()
    {
        var hint = MainWindow.BuildGoogleAuthorizationFailureHint(["invalid_grant"]);

        Assert.Contains("expired or was revoked", hint);
        Assert.Contains("sign in again", hint);
        Assert.DoesNotContain("Client Secret", hint);
    }

    [Fact]
    public void UnrelatedFailureAddsNoOAuthAdvice()
    {
        Assert.Empty(MainWindow.BuildGoogleAuthorizationFailureHint(["Network unreachable"]));
    }
}
