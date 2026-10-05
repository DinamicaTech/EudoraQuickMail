using System.Diagnostics;

namespace QuickMail.Services;

/// <summary>Operation-local progress; unrelated refresh/sync status cannot overwrite it.</summary>
internal static class FilterOperationTrace
{
    private sealed record Context(string Id, Action<string> Report);
    private static readonly AsyncLocal<Context?> Current = new();
    internal static IDisposable Begin(Action<string> report)
    {
        var previous = Current.Value;
        Current.Value = new Context(Guid.NewGuid().ToString("N")[..8], report);
        return new Scope(() => Current.Value = previous);
    }
    internal static void Report(string text) => Current.Value?.Report(text);
    internal static async Task<T> RunAsync<T>(string phase, string context, string status, Func<Task<T>> action)
    {
        var details = $"operation={Current.Value?.Id ?? "background"}; {context}";
        var started = Stopwatch.GetTimestamp();
        var outcome = "completed";
        Report(status);
        PerformanceLogService.Marker(phase + " BEGIN", details);
        try { return await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        catch (Exception ex) { outcome = ex.GetType().Name; throw; }
        finally { PerformanceLogService.Record(phase + " END", Stopwatch.GetElapsedTime(started), details + "; outcome=" + outcome); }
    }
    internal static Task RunAsync(string phase, string context, string status, Func<Task> action) =>
        RunAsync(phase, context, status, async () => { await action().ConfigureAwait(false); return true; });
    private sealed class Scope(Action restore) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; restore(); }
    }
}
