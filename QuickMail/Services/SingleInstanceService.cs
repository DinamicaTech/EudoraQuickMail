using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace QuickMail.Services;

/// <summary>
/// Enforces one QuickMail process per profile directory. The first instance holds a named
/// mutex and listens on a named event; any later launch for the same profile signals that
/// event — so the running instance can restore its window from the tray — and exits.
/// Different --profileDir values produce different object names, so deliberate
/// multi-profile use keeps working.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateRequested;
    private readonly EventWaitHandle _activationAcknowledged;
    private readonly string _ownerFile;
    private RegisteredWaitHandle? _waitRegistration;
    private bool _disposed;

    public sealed record ExistingInstanceInfo(int ProcessId, long StartTimeUtcTicks);

    private SingleInstanceService(Mutex mutex, EventWaitHandle activateRequested,
        EventWaitHandle activationAcknowledged, string ownerFile)
    {
        _mutex = mutex;
        _activateRequested = activateRequested;
        _activationAcknowledged = activationAcknowledged;
        _ownerFile = ownerFile;
    }

    /// <summary>
    /// Tries to claim single-instance ownership for the profile selected by <paramref name="args"/>.
    /// Returns the guard on success. Returns null when another instance already owns the profile;
    /// in that case the running instance has been signaled to bring its window to the foreground
    /// and the caller should end the process immediately.
    /// </summary>
    public static SingleInstanceService? TryAcquire(string[] args)
        => TryAcquireCore(args, null, out _);

    /// <summary>
    /// Acquires the profile or asks its current owner to restore its UI and waits for an
    /// acknowledgement posted by that owner's UI thread. A null <paramref name="unresponsive"/>
    /// means the existing instance answered normally; a value identifies a process which did not.
    /// </summary>
    public static SingleInstanceService? TryAcquireWithActivationCheck(string[] args,
        TimeSpan timeout, out ExistingInstanceInfo? unresponsive)
        => TryAcquireCore(args, timeout, out unresponsive);

    private static SingleInstanceService? TryAcquireCore(string[] args, TimeSpan? timeout,
        out ExistingInstanceInfo? unresponsive)
    {
        unresponsive = null;
        var key = ProfileKey(args);
        var mutex = new Mutex(initiallyOwned: true, $@"Local\QuickMail-{key}", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            if (timeout is null)
            {
                SignalExistingInstance(key);
                return null;
            }

            if (SignalExistingInstanceAndWait(key, timeout.Value)) return null;
            unresponsive = ReadOwner(OwnerFileName(key)) ?? new ExistingInstanceInfo(0, 0);
            return null;
        }

        var activateRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName(key));
        var activationAcknowledged = new EventWaitHandle(false, EventResetMode.AutoReset, AcknowledgeEventName(key));
        var ownerFile = OwnerFileName(key);
        WriteOwner(ownerFile);
        return new SingleInstanceService(mutex, activateRequested, activationAcknowledged, ownerFile);
    }

    /// <summary>
    /// Starts listening for activation signals from later launches. The callback fires on a
    /// thread-pool thread; the caller is responsible for marshaling to the UI thread.
    /// </summary>
    public void ListenForActivation(Action onActivateRequested)
    {
        _waitRegistration ??= ThreadPool.RegisterWaitForSingleObject(
            _activateRequested,
            (_, _) =>
            {
                try
                {
                    // The caller must synchronously marshal to the UI thread. Only acknowledge
                    // after that work returns, otherwise a frozen dispatcher would look healthy.
                    onActivateRequested();
                    _activationAcknowledged.Set();
                }
                catch
                {
                    // No acknowledgement: the launching process will offer recovery.
                }
            },
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    // Design note (issue #253): the activation signal is a bare auto-reset event that carries no
    // payload — it tells the running instance "come to the foreground", nothing more. A second
    // launch that arrives with toast-activation arguments (open a specific message) therefore
    // brings the window forward but drops the account/folder/message deep-link. This is a
    // deliberately accepted trade-off, not a bug: the case only occurs when the running instance's
    // in-process toast COM registration has failed AND a stale toast survives to COM-launch a
    // second exe — otherwise activation is delivered in-process and never reaches this path.
    // Forwarding the payload would require real inter-process data transfer (e.g. a named pipe
    // carrying the command line) on the single-instance/startup path, which this app has a history
    // of hangs and zombie processes on; the low likelihood does not justify that added surface.
    // If this is ever revisited, this method (and ActivateEventName) is the seam to extend.
    private static void SignalExistingInstance(string key)
    {
        try
        {
            using var evt = EventWaitHandle.OpenExisting(ActivateEventName(key));
            evt.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The owning instance is mid-startup (mutex created, event not yet) or mid-exit.
            // There is nothing to activate; this launch just ends.
        }
    }

    private static bool SignalExistingInstanceAndWait(string key, TimeSpan timeout)
    {
        try
        {
            using var acknowledge = EventWaitHandle.OpenExisting(AcknowledgeEventName(key));
            // Discard an acknowledgement left by a launcher which exited before consuming it.
            acknowledge.WaitOne(0);
            using var activate = EventWaitHandle.OpenExisting(ActivateEventName(key));
            activate.Set();
            return acknowledge.WaitOne(timeout);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The owner is either still starting or already wedged before IPC initialization.
            Thread.Sleep(timeout);
            return false;
        }
    }

    private static string ActivateEventName(string key) => $@"Local\QuickMail-{key}-activate";
    private static string AcknowledgeEventName(string key) => $@"Local\QuickMail-{key}-acknowledge";
    private static string OwnerFileName(string key) =>
        Path.Combine(Path.GetTempPath(), $"EudoraQuickMail-{key}.owner");

    private static void WriteOwner(string path)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            File.WriteAllText(path,
                $"{process.Id}|{process.StartTime.ToUniversalTime().Ticks}");
        }
        catch
        {
            // Activation still works; recovery will explain that the owner cannot be identified.
        }
    }

    private static ExistingInstanceInfo? ReadOwner(string path)
    {
        try
        {
            var parts = File.ReadAllText(path).Split('|');
            return parts.Length == 2 && int.TryParse(parts[0], out var pid) &&
                   long.TryParse(parts[1], out var ticks)
                ? new ExistingInstanceInfo(pid, ticks)
                : null;
        }
        catch { return null; }
    }

    public static bool TryTerminateUnresponsive(ExistingInstanceInfo owner, out string error)
    {
        error = string.Empty;
        if (owner.ProcessId <= 0 || owner.StartTimeUtcTicks <= 0)
        {
            error = "The unresponsive QuickMail process could not be identified safely.";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            if (!process.ProcessName.Equals("QuickMail", StringComparison.OrdinalIgnoreCase) ||
                process.StartTime.ToUniversalTime().Ticks != owner.StartTimeUtcTicks)
            {
                error = "The process which owns this profile has changed. It was not closed.";
                return false;
            }

            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000))
            {
                error = "Windows did not close the unresponsive QuickMail process within five seconds.";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                   UnauthorizedAccessException or NotSupportedException or
                                   System.ComponentModel.Win32Exception)
        {
            error = $"The unresponsive QuickMail process could not be closed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Derives a fixed-length identity for the profile directory chosen by the command line,
    /// so the kernel object names stay valid regardless of path length or characters.
    /// Letter case and a trailing separator do not change the identity.
    /// </summary>
    internal static string ProfileKey(string[] args)
    {
        var raw = ProfileContext.ParseProfileDir(args);
        string dir;
        if (raw is null)
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");
        }
        else
        {
            // An unparseable path still yields a stable key; startup rejects it with an
            // error dialog before any second instance could matter.
            try { dir = Path.GetFullPath(raw); }
            catch (Exception) { dir = raw; }
        }

        dir = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                 .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dir));
        return Convert.ToHexString(hash, 0, 16);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _waitRegistration?.Unregister(null);
        _activateRequested.Dispose();
        _activationAcknowledged.Dispose();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException)
        {
            // Not owned by this thread — closing the handle below still frees the
            // kernel object once the process exits.
        }
        _mutex.Dispose();
        try
        {
            var owner = ReadOwner(_ownerFile);
            if (owner?.ProcessId == Environment.ProcessId) File.Delete(_ownerFile);
        }
        catch { }
    }
}
