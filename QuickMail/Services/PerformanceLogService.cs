using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace QuickMail.Services;

/// <summary>Low-overhead timing log controlled by the existing Enable logging setting.</summary>
public static class PerformanceLogService
{
    private static readonly object WriteGate = new();
    private static string _logFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuickMail", "performance.log");

    public static bool Enabled { get; set; }

    public static void Configure(string profileDir) =>
        _logFile = Path.Combine(profileDir, "performance.log");

    public static IDisposable Measure(string operation, string? details = null) =>
        Enabled ? new TimingScope(operation, details) : NullScope.Instance;

    public static void Record(string operation, TimeSpan elapsed, string? details = null)
    {
        if (!Enabled) return;
        Write(operation, elapsed.TotalMilliseconds, details);
    }

    public static void DeleteLog()
    {
        try
        {
            lock (WriteGate)
                if (File.Exists(_logFile)) File.Delete(_logFile);
        }
        catch { }
    }

    private sealed class TimingScope(string operation, string? details) : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _watch.Stop();
            try
            {
                Write(operation, _watch.Elapsed.TotalMilliseconds, details);
            }
            catch { }
        }
    }

    private static void Write(string operation, double elapsedMilliseconds, string? details)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
            var suffix = string.IsNullOrWhiteSpace(details) ? string.Empty : $" | {details}";
            var line = $"{operation} | {elapsedMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms{suffix} | {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}";
            lock (WriteGate) File.AppendAllText(_logFile, line);
        }
        catch { }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
