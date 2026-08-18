using System.Windows;
using System.Windows.Controls;
using QuickMail.Services;

namespace QuickMail.Views;

public partial class TranslateSelectionWindow : Window
{
    private readonly ProfileContext _profile;
    private readonly TranslationSettingsStore _store;
    private readonly TranslationService _service;
    public string Translation => ResultBox.Text;

    public TranslateSelectionWindow(ProfileContext profile, string text)
    {
        InitializeComponent();
        _profile = profile; _store = new(profile); _service = new(_store); OriginalBox.Text = text;
        SourceBox.ItemsSource = Languages(); TargetBox.ItemsSource = Languages();
        SourceBox.DisplayMemberPath = "Name"; SourceBox.SelectedValuePath = "Code";
        TargetBox.DisplayMemberPath = "Name"; TargetBox.SelectedValuePath = "Code";
        SourceBox.SelectedValue = "es"; TargetBox.SelectedValue = "en";
        ProviderBox.ItemsSource = Enum.GetValues<TranslationProviderKind>();
        ProviderBox.SelectedItem = _store.Load().DefaultProvider;
    }
    private static object[] Languages() => [new { Name="Spanish", Code="es" }, new { Name="Catalan", Code="ca" }, new { Name="English", Code="en" }];
    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Translating…";
        try
        {
            ResultBox.Text = await _service.TranslateAsync(OriginalBox.Text,
                (string)SourceBox.SelectedValue, (string)TargetBox.SelectedValue,
                (TranslationProviderKind)ProviderBox.SelectedItem);
            StatusText.Text = "Translation complete.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Replace_Click(object sender, RoutedEventArgs e)
    { if (!string.IsNullOrWhiteSpace(ResultBox.Text)) DialogResult = true; else StatusText.Text = "Translate the text first."; }
    private void Settings_Click(object sender, RoutedEventArgs e) => new TranslationProvidersWindow(_profile) { Owner = this }.ShowDialog();
    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetBox?.SelectedValue as string == "ca" && ProviderBox.SelectedItem is TranslationProviderKind.DeepL)
            StatusText.Text = "Catalan requires Argos Translate.";
    }
}
