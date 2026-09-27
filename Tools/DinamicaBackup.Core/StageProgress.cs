using System.Diagnostics;

namespace DinamicaBackup;

/// <summary>Reports elapsed time, not an estimated percentage, for opaque SQLite work.</summary>
internal sealed class StageProgress : IDisposable
{
    private readonly IProgress<ProgressInfo>? _progress;
    private readonly object _gate = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Timer? _timer;
    private string _stage;
    private bool _disposed;
    public StageProgress(IProgress<ProgressInfo>? progress, string stage)
    {
        _progress = progress; _stage = stage;
        Report();
        if (progress != null) _timer = new Timer(_ => { lock (_gate) { if (!_disposed) Report(); } }, null, 1000, 1000);
    }
    public void Set(string stage)
    {
        lock (_gate) { _stage = stage; _elapsed.Restart(); Report(); }
    }
    private void Report() => _progress?.Report(new ProgressInfo(_stage + " | Elapsed: " + _elapsed.Elapsed.ToString(@"hh\:mm\:ss")));
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _timer?.Dispose(); }
    }
}
