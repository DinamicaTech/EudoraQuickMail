using QuickMail.Helpers;

namespace QuickMail.Tests;

public sealed class CursorVisibilityRecoveryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    public void VisibleCounterIsUnchanged(int initial)
    {
        var count = initial;
        Assert.Equal(0, CursorVisibilityRecovery.RestoreCounter(show => count += show ? 1 : -1));
        Assert.Equal(initial, count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-15)]
    public void HiddenCounterStopsAtZeroAndRepeatedActivationDoesNotInflateIt(int initial)
    {
        var count = initial;
        Assert.Equal(-initial, CursorVisibilityRecovery.RestoreCounter(show => count += show ? 1 : -1));
        Assert.Equal(0, count);
        Assert.Equal(0, CursorVisibilityRecovery.RestoreCounter(show => count += show ? 1 : -1));
        Assert.Equal(0, count);
    }

    [Fact]
    public void PathologicalCounterCannotHangActivation()
    {
        var calls = 0;
        Assert.Equal(CursorVisibilityRecovery.MaxCorrections,
            CursorVisibilityRecovery.RestoreCounter(_ => { calls++; return -100; }));
        Assert.Equal(CursorVisibilityRecovery.MaxCorrections, calls);
    }
}
