using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class FilterOperationTraceTests
{
    [Fact]
    public async Task ParallelOperationsKeepTheirOwnProgress()
    {
        var first = new List<string>();
        var second = new List<string>();
        async Task Run(List<string> output, string label)
        {
            using var scope = FilterOperationTrace.Begin(output.Add);
            await Task.Yield();
            await FilterOperationTrace.RunAsync("test", "", label, () => Task.CompletedTask);
            FilterOperationTrace.Report(label + " done");
        }
        await Task.WhenAll(Run(first, "first"), Run(second, "second"));
        Assert.Equal(new[] { "first", "first done" }, first);
        Assert.Equal(new[] { "second", "second done" }, second);
    }

    [Fact]
    public void NestedScopeRestoresPreviousProgress()
    {
        var outer = new List<string>();
        var inner = new List<string>();
        using (FilterOperationTrace.Begin(outer.Add))
        {
            using (FilterOperationTrace.Begin(inner.Add))
                FilterOperationTrace.Report("inner");
            FilterOperationTrace.Report("outer");
        }
        FilterOperationTrace.Report("unrelated");
        Assert.Equal(new[] { "outer" }, outer);
        Assert.Equal(new[] { "inner" }, inner);
    }

    [Fact]
    public async Task FailuresAndCancellationAreNotSwallowed()
    {
        var error = new InvalidOperationException("test");
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FilterOperationTrace.RunAsync("test", "", "", () => Task.FromException(error))));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FilterOperationTrace.RunAsync("test", "", "", () => Task.FromCanceled(cancellation.Token)));
    }

    [WpfFact]
    public void SlowOperationWindowUsesContentHeight()
    {
        var window = new QuickMail.Views.SlowOperationWindow("Applying filters", () => "Long progress message");
        Assert.Equal(System.Windows.SizeToContent.Height, window.SizeToContent);
        Assert.True(double.IsNaN(window.Height));
        window.Close();
    }
}
