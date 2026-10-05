using System.Globalization;
using QuickMail.Helpers;

namespace QuickMail.Tests;

public sealed class ScheduledDateParserTests
{
    [Theory]
    [InlineData("es-ES", "12/10/2026", 9, 0)]
    [InlineData("en-US", "10/12/2026", 9, 0)]
    [InlineData("es-ES", "12/10/2026 00:00", 0, 0)]
    [InlineData("es-ES", "12/10/2026 16:45", 16, 45)]
    [InlineData("en-US", "10/12/2026 12:00 AM", 0, 0)]
    [InlineData("en-US", "10/12/2026 4:45 PM", 16, 45)]
    public void MissingTimeDefaultsToMorningButExplicitTimeIsPreserved(string culture, string text, int hour, int minute)
    {
        Assert.True(ScheduledDateParser.TryParse(text, CultureInfo.GetCultureInfo(culture), out var value));
        Assert.Equal(new DateTime(2026, 10, 12, hour, minute, 0), value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("31/02/2026")]
    [InlineData("invalid")]
    public void InvalidDateIsRejected(string text) =>
        Assert.False(ScheduledDateParser.TryParse(text, CultureInfo.GetCultureInfo("es-ES"), out _));
}
