using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace QuickMail.Services;

public sealed record GrammarIssue(int Offset, int Length, string Message, string RuleId,
    IReadOnlyList<string> Replacements);

public sealed class LanguageToolService : IDisposable
{
    private const string DownloadUrl = "https://languagetool.org/download/LanguageTool-stable.zip";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly string _componentDirectory;
    private Process? _server;
    private int _port;

    public LanguageToolService(ProfileContext profile) =>
        _componentDirectory = Path.Combine(profile.ProfileDir, "Components", "LanguageTool");

    public bool IsInstalled => FindServerJar() is not null;

    public async Task InstallAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        Directory.CreateDirectory(_componentDirectory);
        var archive = Path.Combine(_componentDirectory, "LanguageTool-stable.download");
        progress?.Report("Downloading LanguageTool (approximately 252 MB)…");
        using (var response = await Http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[1024 * 128]; long received = 0;
            var total = response.Content.Headers.ContentLength;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token); received += read;
                if (total > 0) progress?.Report($"Downloading LanguageTool… {received * 100 / total}%");
            }
        }

        progress?.Report("Installing LanguageTool…");
        var staging = Path.Combine(_componentDirectory, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(archive, staging);
            foreach (var directory in Directory.GetDirectories(_componentDirectory, "LanguageTool-*"))
                Directory.Delete(directory, true);
            var extracted = Directory.GetDirectories(staging, "LanguageTool-*").SingleOrDefault()
                ?? throw new InvalidDataException("The LanguageTool package has an unexpected layout.");
            Directory.Move(extracted, Path.Combine(_componentDirectory, Path.GetFileName(extracted)));
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        progress?.Report("LanguageTool installed.");
    }

    public async Task<IReadOnlyList<GrammarIssue>> CheckAsync(string text, string language,
        CancellationToken token = default)
    {
        if (!IsInstalled) throw new InvalidOperationException("LanguageTool is not installed.");
        await EnsureServerAsync(token);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["language"] = language,
            ["text"] = text,
        });
        using var response = await Http.PostAsync($"http://127.0.0.1:{_port}/v2/check", content, token);
        response.EnsureSuccessStatusCode();
        return ParseIssues(await response.Content.ReadAsStringAsync(token));
    }

    internal static IReadOnlyList<GrammarIssue> ParseIssues(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("matches").EnumerateArray().Select(match =>
            new GrammarIssue(
                match.GetProperty("offset").GetInt32(), match.GetProperty("length").GetInt32(),
                match.GetProperty("message").GetString() ?? "Grammar issue",
                match.GetProperty("rule").GetProperty("id").GetString() ?? string.Empty,
                match.GetProperty("replacements").EnumerateArray()
                    .Select(value => value.GetProperty("value").GetString() ?? string.Empty)
                    .Where(value => value.Length > 0).Take(8).ToArray())).ToArray();
    }

    public static string ApplyPreferredReplacements(string text, IEnumerable<GrammarIssue> issues)
    {
        foreach (var issue in issues.Where(issue => issue.Replacements.Count > 0)
                     .OrderByDescending(issue => issue.Offset))
        {
            if (issue.Offset < 0 || issue.Length < 0 || issue.Offset + issue.Length > text.Length) continue;
            text = string.Concat(text.AsSpan(0, issue.Offset), issue.Replacements[0],
                text.AsSpan(issue.Offset + issue.Length));
        }
        return text;
    }

    private async Task EnsureServerAsync(CancellationToken token)
    {
        if (_server is { HasExited: false }) return;
        var jar = FindServerJar() ?? throw new InvalidOperationException("LanguageTool is not installed.");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        _port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var psi = new ProcessStartInfo("java")
        {
            WorkingDirectory = Path.GetDirectoryName(jar)!, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { "-Xmx512m", "-cp", Path.GetFileName(jar),
                     "org.languagetool.server.HTTPServer", "--port", _port.ToString() })
            psi.ArgumentList.Add(argument);
        _server = Process.Start(psi) ?? throw new InvalidOperationException("LanguageTool could not be started.");
        for (var attempt = 0; attempt < 80; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (_server.HasExited) throw new InvalidOperationException(
                $"LanguageTool stopped during startup: {await _server.StandardError.ReadToEndAsync(token)}");
            try
            {
                using var response = await Http.GetAsync($"http://127.0.0.1:{_port}/v2/languages", token);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(250, token);
        }
        throw new TimeoutException("LanguageTool did not start in time.");
    }

    private string? FindServerJar() => Directory.Exists(_componentDirectory)
        ? Directory.GetFiles(_componentDirectory, "languagetool-server.jar", SearchOption.AllDirectories)
            .FirstOrDefault()
        : null;

    public void Dispose()
    {
        if (_server is { HasExited: false })
            try { _server.Kill(true); } catch { }
        _server?.Dispose();
    }
}
