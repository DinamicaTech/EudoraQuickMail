using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public class MessageLinkDomainTests
{
    [Theory]
    [InlineData("https://example.com/order", "Sales <sales@example.com>")]
    [InlineData("https://www.example.com/order", "sales@example.com")]
    [InlineData("https://example.com/order", "sales@mail.example.com")]
    public void SameDomainOrSubdomain_IsNotForeign(string url, string from) =>
        Assert.False(MessageLinkDomain.IsForeign(url, from));

    [Theory]
    [InlineData("https://example.com.evil.test/login", "sales@example.com")]
    [InlineData("https://tracking.test/click", "sales@example.com")]
    public void DifferentDomain_IsForeign(string url, string from) =>
        Assert.True(MessageLinkDomain.IsForeign(url, from));

    [Fact]
    public void LinkWithoutHost_DoesNotProduceDomainWarning() =>
        Assert.False(MessageLinkDomain.IsForeign("mailto:support@example.com", "sales@example.com"));
}
