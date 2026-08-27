using System;
using System.Threading;

namespace QuickMail.Helpers;

/// <summary>Classifies the one benign exception race that can occur after shutdown has begun.</summary>
internal static class ShutdownExceptionPolicy
{
    internal static bool IsCancellationTokenSourceDisposal(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is not ObjectDisposedException disposed) continue;

            if (string.Equals(disposed.ObjectName, nameof(CancellationTokenSource),
                    StringComparison.Ordinal) ||
                disposed.Message.Contains(nameof(CancellationTokenSource),
                    StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
