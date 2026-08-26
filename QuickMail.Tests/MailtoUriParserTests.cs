using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public class MailtoUriParserTests
{
    [Fact]
    public void Parse_PopulatesRecipientsSubjectAndBody()
    {
        var model = MailtoUriParser.Parse(
            "mailto:first@example.com?to=second%40example.com&cc=copy%40example.com" +
            "&bcc=hidden%40example.com&subject=Hello%20world&body=First%20line%0ASecond%20line");

        Assert.NotNull(model);
        Assert.Equal("first@example.com, second@example.com", model.To);
        Assert.Equal("copy@example.com", model.Cc);
        Assert.Equal("hidden@example.com", model.Bcc);
        Assert.Equal("Hello world", model.Subject);
        Assert.Equal("First line\nSecond line", model.Body.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ParseArguments_AcceptsExplicitSwitchAndDirectProtocolUri()
    {
        var switched = MailtoUriParser.ParseArguments(
            ["--profileDir", @"C:\Mail", "--mailto", "mailto:one@example.com"]);
        var direct = MailtoUriParser.ParseArguments(["mailto:two@example.com"]);

        Assert.Equal("one@example.com", switched?.To);
        Assert.Equal("two@example.com", direct?.To);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com")]
    [InlineData("mailto:%ZZ")]
    public void Parse_RejectsInvalidActivation(string? value)
    {
        Assert.Null(MailtoUriParser.Parse(value));
    }

    [Fact]
    public void Parse_RemovesLineBreaksFromHeaderFieldsButPreservesBodyBreaks()
    {
        var model = MailtoUriParser.Parse(
            "mailto:user@example.com?subject=Safe%0D%0ASubject&body=One%0D%0ATwo");

        Assert.Equal("Safe  Subject", model?.Subject);
        Assert.Equal("One\r\nTwo", model?.Body);
    }
}
