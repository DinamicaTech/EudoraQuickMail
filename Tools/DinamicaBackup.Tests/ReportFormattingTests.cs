using DinamicaBackup;

public sealed class ReportFormattingTests
{
    [Theory]
    [InlineData(916.6369023, "00:15:16")]
    [InlineData(0.9, "00:00:00")]
    [InlineData(90061, "25:01:01")]
    public void DurationHasNoFractionalSeconds(double seconds, string expected)
        => Assert.Equal(expected, ReportFormatting.Duration(TimeSpan.FromSeconds(seconds)));
    [Fact] public void UnfinishedRunHasNoInventedDuration()
        => Assert.Equal("—", ReportFormatting.Duration(null));
}
