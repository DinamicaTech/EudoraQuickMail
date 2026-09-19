using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace QuickMail.Views;

public partial class UnsubscribeBrowserWindow : Window
{
    // This host is deliberately the isolated alternative to ExternalUriPolicy: unsubscribe pages
    // stay inside an ephemeral WebView2 profile instead of inheriting browser cookies or sessions.
    private readonly Uri _initialUri;
    private readonly string _profilePath;

    public UnsubscribeBrowserWindow(Uri initialUri)
    {
        InitializeComponent();
        _initialUri = initialUri;
        _profilePath = Path.Combine(Path.GetTempPath(), "QuickMail", "Unsubscribe", Guid.NewGuid().ToString("N"));
        AddressField.Text = initialUri.AbsoluteUri;
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_profilePath);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profilePath);
            await Browser.EnsureCoreWebView2Async(environment);
            var settings = Browser.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            Browser.CoreWebView2.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            Browser.CoreWebView2.DownloadStarting += (_, args) => args.Cancel = true;
            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && IsWebUri(uri))
                    Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
            };
            Browser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !IsWebUri(uri))
                    args.Cancel = true;
            };
            Browser.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                AddressField.Text = Browser.Source?.AbsoluteUri ?? _initialUri.AbsoluteUri;
                LoadingText.Visibility = Visibility.Collapsed;
                Browser.Visibility = Visibility.Visible;
            };
            Browser.CoreWebView2.Navigate(_initialUri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            LoadingText.Text = $"The isolated browser could not be opened: {ex.Message}";
        }
    }

    private static bool IsWebUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.F6)
        {
            if (Keyboard.FocusedElement == AddressField) Browser.Focus(); else AddressField.Focus();
            e.Handled = true;
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        Browser.Dispose();
        try { if (Directory.Exists(_profilePath)) Directory.Delete(_profilePath, recursive: true); }
        catch { /* WebView2 can retain profile files briefly; Windows temp cleanup remains the fallback. */ }
    }
}
