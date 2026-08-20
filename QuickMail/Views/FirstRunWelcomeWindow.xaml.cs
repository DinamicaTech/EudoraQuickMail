using System.IO;
using System.Windows;

namespace QuickMail.Views;

public partial class FirstRunWelcomeWindow : Window
{
    private readonly bool _importOnly;
    public enum WelcomeChoice { None, CreateAccount, ImportEudora }

    public WelcomeChoice Choice { get; private set; }
    public string EudoraRoot => Path.GetDirectoryName(EudoraExeBox.Text.Trim()) ?? string.Empty;
    public string DataFolder => DataFolderBox.Text.Trim();
    public string RootDisplayName => RootNameBox.Text.Trim();
    public string AttachmentMode => MoveAttachments.IsChecked == true ? "move"
        : CopyAttachments.IsChecked == true ? "copy" : "keep";

    public FirstRunWelcomeWindow(string defaultDataFolder, bool importOnly = false)
    {
        InitializeComponent();
        _importOnly = importOnly;
        DataFolderBox.Text = defaultDataFolder;
        if (importOnly)
        {
            Title = "Import from Eudora";
            HeadingText.Text = "Import from Eudora";
            IntroductionText.Text = "Import Eudora accounts, folders, messages and referenced attachments. Eudora should be closed during the import.";
            WithoutEudoraButton.Content = "Cancel";
        }
    }

    private void BrowseEudora_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Eudora.exe", Filter = "Eudora executable (Eudora.exe)|Eudora.exe",
            CheckFileExists = true, Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true) EudoraExeBox.Text = dialog.FileName;
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
        if (!File.Exists(EudoraExeBox.Text.Trim()) ||
            !Path.GetFileName(EudoraExeBox.Text.Trim()).Equals("Eudora.exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Select Eudora.exe from the root of your Eudora data folder.",
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
        var confirmation = MessageBox.Show(this,
            $"All Eudora messages from the following folder will be imported:\n\n{EudoraRoot}\n\n" +
            $"Eudora QuickMail data folder:\n{DataFolder}\n\n" +
            "Any previous Eudora import in that data folder will be replaced. Other accounts and messages are preserved. " +
            "Eudora QuickMail will close during the import and reopen automatically when it finishes. Continue?",
            "Import from Eudora", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.Yes) return;
        Choice = WelcomeChoice.ImportEudora;
        DialogResult = true;
    }
}
