using System.Text.Json;
using System.IO;

namespace QuickMail.Services;

public enum MailActivityMode { Online, Offline, Focus }

/// <summary>Application-wide send/receive policy persisted per profile.</summary>
public sealed class MailActivityPolicyService : IDisposable
{
    private sealed record State(MailActivityMode Mode, DateTimeOffset? FocusUntilUtc);
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private State _state;
    private int _disposed;

    public MailActivityPolicyService(ProfileContext profile)
    {
        _path = Path.Combine(profile.ProfileDir, "mail-activity.json");
        _state = Load();
        NormalizeExpiredFocus();
        _timer = new Timer(_ => CheckExpiry(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public event Action? Changed;
    public MailActivityMode Mode { get { lock (_gate) { NormalizeExpiredFocus(); return _state.Mode; } } }
    public DateTimeOffset? FocusUntilUtc { get { lock (_gate) { NormalizeExpiredFocus(); return _state.FocusUntilUtc; } } }
    public bool CanReceive => Mode == MailActivityMode.Online;
    public bool CanSend => Mode != MailActivityMode.Offline;
    public string DisplayText => Mode switch
    {
        MailActivityMode.Offline => "Offline — sending and receiving are paused",
        MailActivityMode.Focus => $"Focus mode — receiving paused until {FocusUntilUtc?.ToLocalTime():t}",
        _ => "Online"
    };

    public void SetOffline() => Set(new(MailActivityMode.Offline, null));
    public void StartFocus(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        Set(new(MailActivityMode.Focus, DateTimeOffset.UtcNow.Add(duration)));
    }
    public void GoOnline() => Set(new(MailActivityMode.Online, null));

    private void Set(State state)
    {
        lock (_gate)
        {
            if (_state == state) return;
            _state = state;
            Save();
        }
        Changed?.Invoke();
    }

    private void CheckExpiry()
    {
        var changed = false;
        lock (_gate)
        {
            if (_state.Mode == MailActivityMode.Focus && _state.FocusUntilUtc <= DateTimeOffset.UtcNow)
            {
                _state = new(MailActivityMode.Online, null);
                Save();
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    private void NormalizeExpiredFocus()
    {
        if (_state.Mode == MailActivityMode.Focus && _state.FocusUntilUtc <= DateTimeOffset.UtcNow)
            _state = new(MailActivityMode.Online, null);
    }

    private State Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new(MailActivityMode.Online, null)
                : new(MailActivityMode.Online, null);
        }
        catch (Exception ex)
        {
            LogService.Log("Load mail activity mode", ex);
            return new(MailActivityMode.Online, null);
        }
    }

    private void Save() => AtomicJsonFile.Write(_path, _state);
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _timer.Dispose(); }
}
