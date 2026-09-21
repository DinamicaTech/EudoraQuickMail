using System.ComponentModel;
using System.IO;
using System.Windows;
using QuickMail.Services;

namespace QuickMail.Views;

public partial class QuickMailProfileImportWindow : Window
{
    private bool _isBusy;
    public bool ImportCompleted { get; private set; }
    public string ImportedProfile { get; private set; } = string.Empty;
    public QuickMailProfileImportResult? Result { get; private set; }

    public QuickMailProfileImportWindow(string defaultDestinationProfile)
    {
        InitializeComponent();
        SourceBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");
        DestinationBox.Text = defaultDestinationProfile;
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the existing QuickMail profile",
            Multiselect = false,
            InitialDirectory = Directory.Exists(SourceBox.Text.Trim()) ? SourceBox.Text.Trim() : null,
        };
        if (dialog.ShowDialog(this) == true) SourceBox.Text = dialog.FolderName;
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a new Eudora QuickMail profile folder",
            Multiselect = false,
            InitialDirectory = Directory.Exists(DestinationBox.Text.Trim())
                ? DestinationBox.Text.Trim()
                : Path.GetDirectoryName(DestinationBox.Text.Trim()),
        };
        if (dialog.ShowDialog(this) == true) DestinationBox.Text = dialog.FolderName;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var source = SourceBox.Text.Trim();
        var destination = DestinationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
        {
            MessageBox.Show(this, "Select both the existing QuickMail profile and a new Eudora QuickMail profile folder.",
                "Import QuickMail Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirmation = MessageBox.Show(this,
            $"QuickMail profile:\n{source}\n\nNew Eudora QuickMail profile:\n{destination}\n\n" +
            "QuickMail must be closed. The source profile will remain unchanged. Continue?",
            "Import QuickMail Profile", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.Yes) return;

        SetBusy(true);
        var progress = new Progress<string>(message => StatusText.Text = message);
        try
        {
            Result = await new QuickMailProfileImporter().ImportAsync(
                new QuickMailProfileImportOptions(source, destination,
                    StartOfflineCheckBox.IsChecked == true), progress);
            ImportCompleted = true;
            ImportedProfile = Result.DestinationProfile;
            MessageBox.Show(this,
                $"QuickMail profile imported successfully.\n\nFiles copied: {Result.FilesCopied:N0}\n" +
                $"Data copied: {Result.BytesCopied / 1_048_576.0:N2} MB\n\n" +
                (StartOfflineCheckBox.IsChecked == true
                    ? "The imported profile will start offline. Review its accounts before returning online."
                    : "The imported profile may connect to its configured accounts when it starts."),
                "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            LogService.Log("Import QuickMail profile", ex);
            StatusText.Text = "Import failed. The source profile was not changed.";
            MessageBox.Show(this, ex.Message, "Import QuickMail Profile",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        SourceBox.IsEnabled = !busy;
        DestinationBox.IsEnabled = !busy;
        StartOfflineCheckBox.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        ImportProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) StatusText.Text = "Preparing the import…";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isBusy && !ImportCompleted)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
