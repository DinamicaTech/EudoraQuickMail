using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuickMail.Services;

public enum TranslationProviderKind { Argos, DeepL }

public sealed class TranslationSettings
{
    public TranslationProviderKind DefaultProvider { get; set; } = TranslationProviderKind.Argos;
    public string ArgosCommand { get; set; } = "argos-translate";
    public string EncryptedDeepLKey { get; set; } = string.Empty;
}

public sealed class TranslationSettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuickMail.Translation.v1");
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private readonly string _path;

    public TranslationSettingsStore(ProfileContext profile) =>
        _path = Path.Combine(profile.ProfileDir, "translation.json");

    public TranslationSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<TranslationSettings>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public void Save(TranslationSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Helpers.AtomicFile.WriteAllText(_path,
            JsonSerializer.Serialize(settings, IndentedJson), Encoding.UTF8);
    }

    public string? GetDeepLKey(TranslationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.EncryptedDeepLKey)) return null;
        try
        {
            var clear = ProtectedData.Unprotect(Convert.FromBase64String(settings.EncryptedDeepLKey),
                Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }

    public void SetDeepLKey(TranslationSettings settings, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) { settings.EncryptedDeepLKey = string.Empty; return; }
        var clear = Encoding.UTF8.GetBytes(key.Trim());
        try
        {
            settings.EncryptedDeepLKey = Convert.ToBase64String(ProtectedData.Protect(clear, Entropy,
                DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}

public sealed class TranslationService
{
    private readonly TranslationSettingsStore _store;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    public TranslationService(TranslationSettingsStore store) => _store = store;

    public async Task<string> TranslateAsync(string text, string source, string target,
        TranslationProviderKind provider, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Select some text first.");
        if (source == target) return text;
        return provider == TranslationProviderKind.Argos
            ? await TranslateWithArgosAsync(text, source, target, cancellationToken)
            : await TranslateWithDeepLAsync(text, source, target, cancellationToken);
    }

    public async Task<string> TestAsync(TranslationProviderKind provider, CancellationToken token = default)
    {
        var translated = await TranslateAsync("Hello", "en", "es", provider, token);
        return $"Connection successful. Test result: {translated}";
    }

    public async Task<string> GetDeepLUsageAsync(CancellationToken token = default)
    {
        var settings = _store.Load();
        var key = _store.GetDeepLKey(settings);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No DeepL API key is configured.");
        var host = key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/usage" : "https://api.deepl.com/v2/usage";
        using var request = new HttpRequestMessage(HttpMethod.Get, host);
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
        using var response = await Http.SendAsync(request, token);
        var json = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"DeepL usage query failed: {json}");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var used = root.GetProperty("character_count").GetInt64();
        var limit = root.GetProperty("character_limit").GetInt64();
        return $"DeepL usage: {used:N0} of {limit:N0} characters; {Math.Max(0, limit - used):N0} available.";
    }

    private async Task<string> TranslateWithArgosAsync(string text, string source, string target, CancellationToken token)
    {
        var settings = _store.Load();
        var command = string.IsNullOrWhiteSpace(settings.ArgosCommand) ? "argos-translate" : settings.ArgosCommand;
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--from"); psi.ArgumentList.Add(source);
        psi.ArgumentList.Add("--to"); psi.ArgumentList.Add(target);
        psi.ArgumentList.Add(text);
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Argos Translate could not be started.");
            var outputTask = process.StandardOutput.ReadToEndAsync(token);
            var errorTask = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Argos Translate failed." : error);
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("Argos Translate is not installed or its command cannot be found. Open Translation Providers to configure it.");
        }
    }

    private async Task<string> TranslateWithDeepLAsync(string text, string source, string target, CancellationToken token)
    {
        if (target == "ca" || source == "ca")
            throw new InvalidOperationException("DeepL does not currently expose Catalan in its text translation API. Select Argos Translate for Catalan.");
        var settings = _store.Load();
        var key = _store.GetDeepLKey(settings);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No DeepL API key is configured. Open Translation Providers to add one.");
        var host = key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/translate" : "https://api.deepl.com/v2/translate";
        using var request = new HttpRequestMessage(HttpMethod.Post, host);
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
        request.Content = JsonContent.Create(new
        {
            text = new[] { text },
            source_lang = source.ToUpperInvariant(),
            target_lang = target.ToUpperInvariant(),
            preserve_formatting = true,
        });
        using var response = await Http.SendAsync(request, token);
        var json = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepL returned {(int)response.StatusCode}: {DeepLError(json)}");
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("translations")[0].GetProperty("text").GetString() ?? string.Empty;
    }

    private static string DeepLError(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.GetProperty("message").GetString() ?? json; }
        catch { return json; }
    }
}
