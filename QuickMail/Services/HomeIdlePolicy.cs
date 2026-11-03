namespace QuickMail.Services;

/// <summary>One transition per idle episode; blocked transitions remain pending.</summary>
public sealed class HomeIdlePolicy
{
    private TimeSpan _lastInput;
    private bool _shown;
    public void Input(TimeSpan now) { _lastInput = now; _shown = false; }
    public bool ShouldShow(TimeSpan now, bool enabled, bool blocked)
    {
        if (!enabled) { Input(now); return false; }
        if (blocked || _shown || now - _lastInput < TimeSpan.FromMinutes(15)) return false;
        _shown = true;
        return true;
    }
}
