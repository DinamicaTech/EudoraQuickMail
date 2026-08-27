using QuickMail.Helpers;

namespace QuickMail.Tests;

public sealed class ShutdownExceptionPolicyTests
{
    [Fact]
    public void RecognizesCancellationTokenSourceDisposal()
    {
        var exception = new ObjectDisposedException(nameof(CancellationTokenSource));

        Assert.True(ShutdownExceptionPolicy.IsCancellationTokenSourceDisposal(exception));
    }

    [Fact]
    public void RecognizesWrappedCancellationTokenSourceDisposal()
    {
        var exception = new InvalidOperationException("outer",
            new ObjectDisposedException(nameof(CancellationTokenSource)));

        Assert.True(ShutdownExceptionPolicy.IsCancellationTokenSourceDisposal(exception));
    }

    [Fact]
    public void RejectsOtherDisposedObjectsAndOtherExceptions()
    {
        Assert.False(ShutdownExceptionPolicy.IsCancellationTokenSourceDisposal(
            new ObjectDisposedException("HttpClient")));
        Assert.False(ShutdownExceptionPolicy.IsCancellationTokenSourceDisposal(
            new InvalidOperationException("not disposed")));
    }
}
