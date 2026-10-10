using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using Velopack;
using Velopack.Sources;

namespace QuickMail.Services;

public class UpdateCheckService : IUpdateCheckService, IDisposable
{
    private const string StandaloneMarkerFile = "quickmail-standalone-install";
    // Single source of truth for the project location — VelopackRuntime and MainViewModel
    // build their URLs from these rather than repeating the literal.
    public const string RepoUrl = "https://github.com/DinamicaTech/EudoraQuickMail";
    public const string ReleasesPageUrl = $"{RepoUrl}/releases";
    private const string ApiUrl = "https://api.github.com/repos/DinamicaTech/EudoraQuickMail/releases/latest";

    /// <summary>The release-notes page for a specific version — the "what's new" link.</summary>
    public static string ReleaseTagUrl(string version) => $"{ReleasesPageUrl}/tag/v{version}";

    // Reflection result is static for the lifetime of the app — compute once. Uses the shared
    // display version so a hotfix build (e.g. 0.7.9.1) compares at full precision and does not
    // treat its own release as a newer version.
    private static readonly string _currentVersion = Helpers.AppVersion.Display;

    private readonly HttpClient _http;
    // Internal lifetime token: cancelled in Dispose() so an in-flight request on app exit
    // is cooperatively cancelled (clean OperationCanceledException) rather than left to either
    // the 10s HttpClient timeout or an ObjectDisposedException from disposing _http mid-request.
    private readonly CancellationTokenSource _cts = new();
    // Test/debug override (--updateFeed): a local folder or URL holding vpk pack output,
    // so the full download-and-apply cycle can be exercised without publishing a release.
    private readonly string? _feedOverride;
    // Supplies the AutoUpdate setting; null (tests) behaves as AutoUpdate on.
    private readonly IConfigService? _configService;
    private bool _disposed;

    // Installed and portable update state. Applying always requires explicit confirmation.
    private UpdateManager? _velopackManager;
    private Velopack.UpdateInfo? _pendingUpdate;
    private Task? _downloadTask;
    private volatile bool _downloadCompleted;
    private PortableRelease? _portableRelease;
    private string? _portableWork;

    public UpdateCheckService(IConfigService? configService = null, string? updateFeedOverride = null)
    {
        _configService = configService;
        _feedOverride = updateFeedOverride;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("QuickMail", _currentVersion));
    }

    public bool SelfUpdatePending => _pendingUpdate is not null || _portableRelease is not null;

    public string? DisabledReason => File.Exists(Path.Combine(AppContext.BaseDirectory, StandaloneMarkerFile))
        ? $"Updates are disabled by {Path.Combine(AppContext.BaseDirectory, StandaloneMarkerFile)}. This marker is used for isolated test installations. Remove it only if this installation should receive official releases, then check again."
        : null;

    public async Task PrepareUpdateAsync(IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        if (_portableRelease is { } portable)
        {
            if (_portableWork != null && !File.Exists(Path.Combine(_portableWork, "abort")))
            {
                progress?.Report("Downloaded update is ready to install.");
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            _portableWork = await PortableUpdateService.PrepareAsync(portable, progress, timeout.Token).ConfigureAwait(false);
            return;
        }
        if (_velopackManager is not { } mgr || _pendingUpdate is not { } update)
            throw new InvalidOperationException("No update is available.");
        progress?.Report("Downloading update…");
        if (_downloadTask is { } dl) await dl.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!_downloadCompleted)
        {
            await mgr.DownloadUpdatesAsync(update, progress: p => progress?.Report($"Downloading: {p}%"),
                cancelToken: cancellationToken).ConfigureAwait(false);
            _downloadCompleted = true;
        }
        progress?.Report("Update verified and ready to install.");
    }

    public async Task<Models.UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        // Local test installers deliberately have no release feed. A marker beside the executable
        // keeps them completely detached from both Velopack's GitHub source and the portable API
        // fallback, while production packages (which omit it) retain normal update behaviour.
        if (DisabledReason is { } reason)
            throw new InvalidOperationException(reason);

        // Velopack path only applies when running from a Velopack install
        // (%LocalAppData%\QuickMail\current\). Portable copies use the official complete ZIP.
        // Manager construction probes the install layout on disk — keep it off the caller's
        // (UI) thread; this method is invoked synchronously from window load.
        var mgr = await Task.Run(CreateVelopackManager, cancellationToken).ConfigureAwait(false);
        if (mgr is not null)
            return await CheckViaVelopackAsync(mgr, cancellationToken).ConfigureAwait(false);

        return await CheckViaGitHubApiAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RestartToUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_portableWork is { } work)
        {
            await PortableUpdateService.ArmAsync(work, cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.WriteAllText(Path.Combine(work, "commit"), "apply after normal exit");
            }
            catch
            {
                File.WriteAllText(Path.Combine(work, "abort"), "cancelled");
                throw;
            }
            return true;
        }
        var mgr = _velopackManager;
        var update = _pendingUpdate;
        if (mgr is null || update is null || !_downloadCompleted) return false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Preserve /debug, --profileDir, --updateFeed across the update-triggered relaunch.
            mgr.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: false, restart: true,
                restartArgs: Environment.GetCommandLineArgs()[1..]);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false; // dialog dismissed — not a failure, no announcement warranted
        }
        catch (Exception ex)
        {
            LogService.Log($"UpdateCheckService: restart to update failed: {ex.Message}");
            return false;
        }
    }

    private UpdateManager? CreateVelopackManager()
    {
        try
        {
            UpdateManager mgr;
            if (_feedOverride is null)
            {
                mgr = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
            }
            else
            {
                LogService.Log($"UpdateCheckService: using update feed override: {_feedOverride}");
                mgr = new UpdateManager(_feedOverride);
            }
            return mgr.IsInstalled ? mgr : null;
        }
        catch (Exception ex)
        {
            LogService.Debug($"UpdateCheckService: Velopack manager unavailable: {ex.Message}");
            return null;
        }
    }

    private async Task<Models.UpdateInfo?> CheckViaVelopackAsync(UpdateManager mgr, CancellationToken cancellationToken)
    {
        try
        {
            // Velopack 1.2.0's CheckForUpdatesAsync takes no CancellationToken, so honor the
            // caller's bound (and Dispose-time cancellation) by abandoning the wait — the
            // underlying request is orphaned but the contract that the check is time-bounded
            // holds, matching the GitHub API path below.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            var update = await mgr.CheckForUpdatesAsync().WaitAsync(linked.Token).ConfigureAwait(false);
            if (update is null)
                return null;

            var version = update.TargetFullRelease.Version.ToString();
            var whatsNewUrl = ReleaseTagUrl(version);

            if (_pendingUpdate?.TargetFullRelease.Version.ToString() == version)
                return new Models.UpdateInfo(version, whatsNewUrl);
            _downloadCompleted = false;
            _downloadTask = null;

            _velopackManager = mgr;
            _pendingUpdate = update;
            _portableRelease = null;

            // AutoUpdate off disables background downloading, not the explicit install button.
            if (_configService?.Load().AutoUpdate == false)
                return new Models.UpdateInfo(version, whatsNewUrl);

            // Optional background staging; never apply just because the app exits for backup.
            var lifetimeToken = _cts.Token;
            _downloadTask = Task.Run(async () =>
            {
                try
                {
                    await mgr.DownloadUpdatesAsync(update, cancelToken: lifetimeToken).ConfigureAwait(false);
                    _downloadCompleted = true;
                    LogService.Log($"Update {version} downloaded; waiting for installation confirmation.");
                }
                catch (Exception ex)
                {
                    LogService.Debug($"UpdateCheckService: background download failed: {ex.Message}");
                }
            }, lifetimeToken);   // service lifetime, not the caller's short check timeout

            // The release-tag page serves as the "what's new" link in the update dialog.
            return new Models.UpdateInfo(version, whatsNewUrl);
        }
        catch (Exception ex)
        {
            LogService.Debug($"UpdateCheckService: Velopack check failed: {ex.Message}");
            throw;
        }
    }

    private async Task<Models.UpdateInfo?> CheckViaGitHubApiAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            var json = await _http.GetStringAsync(ApiUrl, linked.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl) ||
                !root.TryGetProperty("html_url", out var urlEl))
                throw new InvalidDataException("The release response is missing its version or URL.");

            var tag = tagEl.GetString()?.TrimStart('v') ?? "";
            var url = urlEl.GetString() ?? "";

            if (!Version.TryParse(tag, out var remote) ||
                !Version.TryParse(_currentVersion, out var current))
                throw new InvalidDataException("The release version could not be compared with the running version.");

            if (remote <= current) return null;
            if (_portableRelease?.Version != tag) _portableWork = null;
            _portableRelease = PortableUpdateService.FindRelease(root, tag,
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            return new Models.UpdateInfo(tag, url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException or ObjectDisposedException)
        {
            LogService.Debug($"UpdateCheckService: {ex.Message}");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Applying requires an explicit, drained shutdown. In particular, a backup shutdown
        // must never race an automatic updater replacing the application/backup agent.

        _cts.Cancel();   // signal any in-flight request before releasing the handle
        _cts.Dispose();
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
