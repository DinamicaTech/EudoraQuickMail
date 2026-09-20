using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly string _activationFile;
    private RegisteredWaitHandle? _waitRegistration;
    private bool _disposed;

    public sealed record ExistingInstanceInfo(int ProcessId, long StartTimeUtcTicks);

    private SingleInstanceService(Mutex mutex, EventWaitHandle activateRequested,
        EventWaitHandle activationAcknowledged, string ownerFile, string activationFile)
    {
        _mutex = mutex;
        _activateRequested = activateRequested;
        _activationAcknowledged = activationAcknowledged;
        _ownerFile = ownerFile;
        _activationFile = activationFile;
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
            WriteActivationRequest(ActivationFileName(key), args);
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
        var activationFile = ActivationFileName(key);
        TryDelete(activationFile); // discard payload left by a process which no longer owns the mutex
        WriteOwner(ownerFile);
        return new SingleInstanceService(mutex, activateRequested, activationAcknowledged,
            ownerFile, activationFile);
    }

    /// <summary>
    /// Starts listening for activation signals from later launches. The callback fires on a
    /// thread-pool thread; the caller is responsible for marshaling to the UI thread.
    /// </summary>
    public void ListenForActivation(Action onActivateRequested)
        => ListenForActivation(_ => onActivateRequested());

    /// <summary>
    /// Starts listening for activation signals and supplies the command line from the later
    /// launch. This lets protocol activations such as <c>mailto:</c> reach the already-running
    /// instance instead of merely bringing its window to the foreground.
    /// </summary>
    public void ListenForActivation(Action<string[]> onActivateRequested)
    {
        _waitRegistration ??= ThreadPool.RegisterWaitForSingleObject(
            _activateRequested,
            (_, _) =>
            {
                try
                {
                    // The caller must synchronously marshal to the UI thread. Only acknowledge
                    // after that work returns, otherwise a frozen dispatcher would look healthy.
                    onActivateRequested(ReadActivationRequest(_activationFile));
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
    private static string ActivationFileName(string key) =>
        Path.Combine(Path.GetTempPath(), $"EudoraQuickMail-{key}.activation.json");

    private static void WriteActivationRequest(string path, string[] args)
    {
        // A same-directory replace is atomic, so the owner never observes half-written JSON.
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(args));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            // The activation event still restores the existing window; only the optional
            // command payload is lost if the temporary directory cannot be written.
        }
    }

    private static string[] ReadActivationRequest(string path)
    {
        try
        {
            var args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [];
            TryDelete(path);
            return args;
        }
        catch
        {
            TryDelete(path);
            return [];
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

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
            if (!process.ProcessName.Equals("EudoraQM", StringComparison.OrdinalIgnoreCase) ||
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
        TryDelete(_activationFile);
    }
}
