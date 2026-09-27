using DinamicaBackupAgent;

public sealed class WindowsTaskIdentityTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("ronald", @"RONALD\ronald")]
    [InlineData(@" .\ronald ", @"RONALD\ronald")]
    [InlineData(@"DOMAIN\ronald", @"DOMAIN\ronald")]
    [InlineData("user@example.com", "user@example.com")]
    public void NormalizePreservesExplicitIdentityAndExpandsLocalNames(string input, string expected)
        => Assert.Equal(expected, WindowsTaskIdentity.Normalize(input, "RONALD"));
}
