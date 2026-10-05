using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuickMail.Services;

internal sealed record PortableRelease(string Version, string Url, long Size, string Sha256);

internal static class PortableUpdateService
{
    internal static PortableRelease? FindRelease(JsonElement release, string version, Architecture architecture)
    {
        var rid = architecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => null };
        if (rid == null || !release.TryGetProperty("assets", out var assets)) return null;
        var name = $"EudoraQM-v{version}-win-{rid}.zip";
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (url != $"{UpdateCheckService.RepoUrl}/releases/download/v{version}/{name}") return null;
            if (!asset.TryGetProperty("digest", out var digestElement)) return null;
            var digest = digestElement.GetString() ?? "";
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71
                || !digest[7..].All(Uri.IsHexDigit)) return null;
            var size = asset.GetProperty("size").GetInt64();
            return size is > 0 and < 2_000_000_000 ? new(version, url, size, digest[7..]) : null;
        }
        return null;
    }

    internal static async Task<string> PrepareAsync(PortableRelease release, IProgress<string>? progress, CancellationToken ct)
    {
        if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "EudoraQM.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("In-app portable updating requires the published EudoraQM.exe.");
        var helper = Path.Combine(AppContext.BaseDirectory, "Updater", "QuickMail.Updater.exe");
        if (!File.Exists(helper)) throw new FileNotFoundException("The Updater folder is missing. Install the complete application package.");
        var work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EudoraQuickMail", "Updates", release.Version + "-" + Guid.NewGuid().ToString("N"));
        RequireFreeSpace(work, release.Size + new FileInfo(helper).Length + 100_000_000);
        Directory.CreateDirectory(work);
        try
        {
            File.Copy(helper, Path.Combine(work, "QuickMail.Updater.exe"));
            var archive = Path.Combine(work, "package.zip");
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EudoraQuickMail-Updater/1.0");
            using var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long received = 0;
                int count; var lastPercent = -1;
                while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (received > release.Size) throw new InvalidDataException("Unexpected update download size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    var percent = (int)(received * 100 / release.Size);
                    if (percent != lastPercent) progress?.Report($"Downloading: {percent}% ({received / 1048576} / {release.Size / 1048576} MB)");
                    lastPercent = percent;
                }
                if (received != release.Size) throw new InvalidDataException("Incomplete update download.");
            }
            progress?.Report("Verifying downloaded package…");
            await using (var stream = File.OpenRead(archive))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
                if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update checksum does not match GitHub's published SHA-256.");
            }
            progress?.Report("Preparing application files…");
            using (var zip = ZipFile.OpenRead(archive))
            {
                var unpacked = zip.Entries.Sum(entry => entry.Length);
                var previous = zip.Entries.Where(entry => !entry.FullName.EndsWith('/'))
                    .Select(entry => PortableUpdateFiles.SafePath(AppContext.BaseDirectory, entry.FullName))
                    .Where(File.Exists).Sum(path => new FileInfo(path).Length);
                RequireFreeSpace(work, unpacked + previous + 100_000_000);
                RequireFreeSpace(AppContext.BaseDirectory, unpacked + 100_000_000);
            }
            await Task.Run(() => Extract(archive, Path.Combine(work, "stage"), ct), ct).ConfigureAwait(false);
            File.Delete(archive); // verified/extracted; previous binaries are retained separately by the helper
            return work;
        }
        catch
        {
            // This uniquely-created directory has never been passed to the helper. Remove
            // only our known payload files; never recursively remove a supplied directory.
            foreach (var name in new[] { "package.zip", "QuickMail.Updater.exe" })
                try { File.Delete(Path.Combine(work, name)); } catch (IOException) { }
            var stage = Path.Combine(work, "stage");
            if (Directory.Exists(stage))
                foreach (var file in Directory.EnumerateFiles(stage, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    try { File.Delete(PortableUpdateFiles.SafePath(stage, Path.GetRelativePath(stage, file))); }
                    catch (IOException) { }
            throw;
        }
    }

    private static void RequireFreeSpace(string path, long required)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (drive.AvailableFreeSpace < required)
            throw new IOException($"Not enough free space on {drive.Name}. At least {Math.Ceiling(required / 1048576d):0} MB are needed to update safely.");
    }

    internal static void Extract(string archive, string stage, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            var relative = PortableUpdateFiles.Relative(entry.FullName);
            if (!seen.Add(relative) || (total += entry.Length) > 4_000_000_000)
                throw new InvalidDataException("Invalid update archive.");
            var path = PortableUpdateFiles.SafePath(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
        if (!seen.Contains("EudoraQM.exe")) throw new InvalidDataException("The package has no application executable.");
    }

    internal static async Task ArmAsync(string work, CancellationToken ct)
    {
        using var current = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(work, "request.json"), JsonSerializer.Serialize(new
        {
            Target = AppContext.BaseDirectory,
            Pid = current.Id,
            Started = current.StartTime.ToUniversalTime().Ticks,
            Arguments = Environment.GetCommandLineArgs()[1..],
            WorkingDirectory = Environment.CurrentDirectory
        }));
        var start = new ProcessStartInfo(Path.Combine(work, "QuickMail.Updater.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(work, "request.json"));
        using var helper = Process.Start(start) ?? throw new IOException("Could not start the updater.");
        try
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(work, "ready"))) return;
                if (helper.HasExited) throw new IOException("Updater preparation failed. See " + Path.Combine(work, "result.log"));
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            throw new TimeoutException("The updater did not become ready; the application remains open.");
        }
        catch
        {
            File.WriteAllText(Path.Combine(work, "abort"), "cancelled");
            throw;
        }
    }
}
