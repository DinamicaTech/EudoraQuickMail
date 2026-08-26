using System.IO;
using System.Windows;

namespace QuickMail.Views;

public partial class FirstRunWelcomeWindow : Window
{
    private readonly bool _importOnly;
    private readonly List<string> _selectedSources = [];
    public enum WelcomeChoice { None, CreateAccount, ImportEudora }

    public WelcomeChoice Choice { get; private set; }
    public IReadOnlyList<string> SelectedSources => _selectedSources;
    public bool IsSelectiveImport => _selectedSources.Count > 0 &&
        !_selectedSources[0].EndsWith("Eudora.exe", StringComparison.OrdinalIgnoreCase);
    public string EudoraRoot => FindEudoraRoot(_selectedSources.FirstOrDefault());
    public string DataFolder => DataFolderBox.Text.Trim();
    public string RootDisplayName => RootNameBox.Text.Trim();
    public string AttachmentMode => MoveAttachments.IsChecked == true ? "move"
        : CopyAttachments.IsChecked == true ? "copy" : "keep";
    public bool ImportFilters => ImportFiltersCheckBox.IsChecked == true;
    public bool RespectCheckMailSettings => RespectCheckMailSettingsCheckBox.IsChecked == true;

    public FirstRunWelcomeWindow(string defaultDataFolder, bool importOnly = false)
    {
        InitializeComponent();
        // Use the extra height when it is available, but keep every import option reachable on
        // smaller displays through the options panel's own vertical scrollbar.
        MaxHeight = SystemParameters.WorkArea.Height;
        Height = Math.Min(Height, MaxHeight);
        _importOnly = importOnly;
        DataFolderBox.Text = defaultDataFolder;
        if (importOnly)
        {
            Title = "Import from Eudora";
            HeadingText.Text = "Import from Eudora";
            IntroductionText.Text = "Import Eudora accounts, folders, messages, filters and referenced attachments. Eudora should be closed during the import.";
            WithoutEudoraButton.Content = "Cancel";
        }
    }

    private void BrowseEudora_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Eudora.exe or one or more Eudora mailboxes",
            Filter = "Eudora executable or mailboxes (Eudora.exe;*.mbx)|Eudora.exe;*.mbx|Eudora mailboxes (*.mbx)|*.mbx|All files (*.*)|*.*",
            CheckFileExists = true, Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        if (dialog.FileNames.Any(path => path.EndsWith("Eudora.exe", StringComparison.OrdinalIgnoreCase)))
        {
            _selectedSources.Clear();
            _selectedSources.Add(dialog.FileNames.First(path => path.EndsWith("Eudora.exe", StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            _selectedSources.Clear();
            _selectedSources.AddRange(dialog.FileNames.Where(path => path.EndsWith(".mbx", StringComparison.OrdinalIgnoreCase)));
        }
        UpdateSelectedSources();
    }

    private void BrowseEudoraFolders_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select one or more Eudora .fol folders", Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        var folders = dialog.FolderNames
            .Where(path => path.EndsWith(".fol", StringComparison.OrdinalIgnoreCase)).ToList();
        if (folders.Count == 0)
        {
            MessageBox.Show(this, "Select a folder whose name ends in .fol.", "Selective Eudora Import",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_selectedSources.Count == 1 && _selectedSources[0].EndsWith("Eudora.exe", StringComparison.OrdinalIgnoreCase))
            _selectedSources.Clear();
        foreach (var folder in folders)
            if (!_selectedSources.Contains(folder, StringComparer.OrdinalIgnoreCase)) _selectedSources.Add(folder);
        UpdateSelectedSources();
    }

    private void UpdateSelectedSources()
    {
        EudoraExeBox.Text = string.Join("; ", _selectedSources);
        var selective = IsSelectiveImport;
        FolderTreeGroup.Header = selective ? "Selective import destination" : "Folder tree";
        RootNameLabel.Text = selective ? "Destination folder path" : "Root display name";
        ImportFiltersCheckBox.IsEnabled = !selective;
        RespectCheckMailSettingsCheckBox.IsEnabled = !selective;
        if (selective && _selectedSources.Count > 0)
        {
            ImportFiltersCheckBox.IsChecked = false;
            RespectCheckMailSettingsCheckBox.IsChecked = false;
            RootNameBox.Text = Path.GetFileNameWithoutExtension(_selectedSources[0].TrimEnd(Path.DirectorySeparatorChar));
        }
    }

    private void BrowseData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the Eudora QuickMail data folder", Multiselect = false,
            InitialDirectory = Directory.Exists(DataFolder) ? DataFolder : null,
        };
        if (dialog.ShowDialog(this) == true) DataFolderBox.Text = dialog.FolderName;
    }

    private void WithoutEudora_Click(object sender, RoutedEventArgs e)
    {
        if (_importOnly)
        {
            DialogResult = false;
            return;
        }
        Choice = WelcomeChoice.CreateAccount;
        DialogResult = true;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSources.Count == 0 || _selectedSources.Any(path => !File.Exists(path) && !Directory.Exists(path)))
        {
            MessageBox.Show(this, "Select Eudora.exe, one or more .mbx mailboxes, or one or more .fol folders.",
                "Import Eudora", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(DataFolder) || string.IsNullOrWhiteSpace(RootDisplayName))
        {
            MessageBox.Show(this, "Enter an Eudora QuickMail data folder and a folder-tree display name.",
                "Import Eudora", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (AttachmentMode == "move")
        {
            var first = MessageBox.Show(this,
                "Moving attachments removes Eudora's access to every referenced file after the import succeeds. Continue?",
                "Move Eudora Attachments", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (first != MessageBoxResult.Yes) return;
            var second = MessageBox.Show(this,
                "This is the final confirmation. Keep a backup of your Eudora data. Move the referenced attachments?",
                "Confirm Attachment Move", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (second != MessageBoxResult.Yes) return;
        }
        var sourceDescription = IsSelectiveImport
            ? string.Join("\n", _selectedSources.Select(path => " • " + path))
            : EudoraRoot;
        var confirmation = MessageBox.Show(this,
            $"{(IsSelectiveImport ? "The selected Eudora mailboxes" : "All Eudora messages from the following folder")} will be imported:\n\n{sourceDescription}\n\n" +
            $"Eudora QuickMail data folder:\n{DataFolder}\n\n" +
            (IsSelectiveImport ? $"Destination folder path: {RootDisplayName}\n\n" : string.Empty) +
            $"Import filters: {(ImportFilters ? "Yes" : "No")}\n\n" +
            $"Respect Eudora automatic mail checks: {(RespectCheckMailSettings ? "Yes" : "No")}\n\n" +
            (IsSelectiveImport ? "Existing messages are preserved; matching selected messages are updated. " :
                "Any previous Eudora import in that data folder will be replaced. Other accounts and messages are preserved. ") +
            "Eudora QuickMail will close during the import and reopen automatically when it finishes. Continue?",
            "Import from Eudora", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.Yes) return;
        Choice = WelcomeChoice.ImportEudora;
        DialogResult = true;
    }

    private static string FindEudoraRoot(string? selected)
    {
        if (string.IsNullOrWhiteSpace(selected)) return string.Empty;
        var directory = Directory.Exists(selected) ? new DirectoryInfo(selected) : new FileInfo(selected).Directory;
        for (var current = directory; current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Eudora.ini")) ||
                File.Exists(Path.Combine(current.FullName, "Eudora.exe"))) return current.FullName;
        return directory?.FullName ?? string.Empty;
    }
}
