using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.IO;
using QuickMail.Services;

namespace QuickMail.Views;

public partial class TranslationProvidersWindow : Window
{
    private readonly TranslationSettingsStore _store;
    private readonly TranslationService _service;
    private TranslationSettings _settings;

    public TranslationProvidersWindow(ProfileContext profile)
    {
        InitializeComponent();
        _store = new(profile);
        _service = new(_store);
        _settings = _store.Load();
        ArgosCommandBox.Text = _settings.ArgosCommand;
        DefaultProviderBox.SelectedIndex = _settings.DefaultProvider == TranslationProviderKind.Argos ? 0 : 1;
        DeepLKeyBox.Password = _store.GetDeepLKey(_settings) is null ? string.Empty : "••••••••";
    }

    private void PersistGeneral()
    {
        _settings.ArgosCommand = ArgosCommandBox.Text.Trim();
        _settings.DefaultProvider = (DefaultProviderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "DeepL"
            ? TranslationProviderKind.DeepL : TranslationProviderKind.Argos;
        _store.Save(_settings);
    }

    private async void TestArgos_Click(object sender, RoutedEventArgs e) => await TestAsync(TranslationProviderKind.Argos);
    private async void InstallArgos_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Installing Argos Translate…";
        if (!await RunSetupAsync("py", "-m", "pip", "install", "--user", "--upgrade", "argostranslate")) return;
        var scripts = await FindPythonUserScriptsAsync();
        if (scripts is not null)
        {
            var command = Path.Combine(scripts, "argos-translate.exe");
            if (File.Exists(command)) { ArgosCommandBox.Text = command; PersistGeneral(); }
        }
        StatusText.Text = "Argos Translate runtime installed. You can now install the language models.";
    }

    private async void InstallModels_Click(object sender, RoutedEventArgs e)
    {
        PersistGeneral();
        var configured = _settings.ArgosCommand;
        var directory = Path.GetDirectoryName(configured);
        var manager = string.IsNullOrWhiteSpace(directory) ? "argospm" : Path.Combine(directory, "argospm.exe");
        StatusText.Text = "Updating the Argos model catalogue…";
        if (!await RunSetupAsync(manager, "update")) return;
        foreach (var pair in new[] { "translate-en_es", "translate-es_en", "translate-en_ca", "translate-ca_en" })
        {
            StatusText.Text = $"Installing {pair}…";
            if (!await RunSetupAsync(manager, "install", pair)) return;
        }
        StatusText.Text = "Spanish, Catalan and English models installed.";
    }

    private async Task<bool> RunSetupAsync(string command, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {command}.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var message = process.ExitCode == 0 ? "Operation completed." : (await error).Trim();
            StatusText.Text = string.IsNullOrWhiteSpace(message)
                ? (process.ExitCode == 0 ? "Operation completed." : "Operation failed.") : message;
            return process.ExitCode == 0;
        }
        catch (Exception ex) { StatusText.Text = $"Setup failed: {ex.Message}"; return false; }
    }
    private static async Task<string?> FindPythonUserScriptsAsync()
    {
        var psi = new ProcessStartInfo("py") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("import sysconfig;print(sysconfig.get_path('scripts','nt_user'))");
        using var process = Process.Start(psi); if (process is null) return null;
        var result = (await process.StandardOutput.ReadToEndAsync()).Trim(); await process.WaitForExitAsync();
        return process.ExitCode == 0 && result.Length > 0 ? result : null;
    }
    private async void TestDeepL_Click(object sender, RoutedEventArgs e)
    {
        if (DeepLKeyBox.Password != "••••••••" && !string.IsNullOrWhiteSpace(DeepLKeyBox.Password))
        { _store.SetDeepLKey(_settings, DeepLKeyBox.Password); _store.Save(_settings); }
        await TestAsync(TranslationProviderKind.DeepL);
    }
    private async void CheckDeepLUsage_Click(object sender, RoutedEventArgs e)
    {
        if (DeepLKeyBox.Password != "••••••••" && !string.IsNullOrWhiteSpace(DeepLKeyBox.Password))
        { _store.SetDeepLKey(_settings, DeepLKeyBox.Password); _store.Save(_settings); }
        StatusText.Text = "Checking DeepL usage…";
        try { StatusText.Text = await _service.GetDeepLUsageAsync(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async Task TestAsync(TranslationProviderKind provider)
    {
        PersistGeneral(); StatusText.Text = "Testing…";
        try { StatusText.Text = await _service.TestAsync(provider); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (DeepLKeyBox.Password == "••••••••") return;
        _store.SetDeepLKey(_settings, DeepLKeyBox.Password); _store.Save(_settings);
        DeepLKeyBox.Password = "••••••••"; StatusText.Text = "DeepL key saved securely.";
    }
    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    { _store.SetDeepLKey(_settings, null); _store.Save(_settings); DeepLKeyBox.Clear(); StatusText.Text = "DeepL key removed."; }
    private void Close_Click(object sender, RoutedEventArgs e) { PersistGeneral(); Close(); }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        PersistGeneral();
        base.OnClosing(e);
    }
}
