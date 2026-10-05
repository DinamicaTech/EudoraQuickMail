using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DinamicaBackup;
using Microsoft.Win32;

namespace DinamicaBackupAgent;

public sealed partial class AgentWindow : Window
{
    private readonly BackupJob _job;
    private readonly string _jobPath;
    private readonly List<Action> _commit = [];
    private readonly List<Func<string>> _configurationValues = [];
    private Button? _saveButton;
    private string? _savedConfiguration;
    private bool _needsInitialSave;
    private readonly TabControl _tabs = new();
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly ProgressBar _progress = new() { Height = 10, Margin = new Thickness(8) };
    private readonly Button _cancel = new() { Content = "Cancel / Reopen application", IsEnabled = false, Margin = new Thickness(8), Padding = new Thickness(10, 5, 10, 5) };
    private readonly DataGrid _history = new() { IsReadOnly = true, AutoGenerateColumns = false, MinHeight = 180 };
    private CancellationTokenSource? _operation;

    public AgentWindow(BackupJob job, string jobPath)
    {
        _job = job; _jobPath = Path.GetFullPath(jobPath);
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/DinamicaBackupAgent;component/Assets/DinamicaBackup.ico"));
        var version = typeof(AgentWindow).Assembly.GetName().Version;
        Title = $"Dinamica Backup Agent v{version?.ToString(3)} — " + job.Name; Width = 920; Height = 760; MinWidth = 720; MinHeight = 520;
        var root = new DockPanel { Margin = new Thickness(10) }; Content = root;
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(_status); bottom.Children.Add(_progress); bottom.Children.Add(_actions); bottom.Children.Add(_cancel);
        root.Children.Add(_tabs);
        _cancel.Click += (_, _) => { _operation?.Cancel(); _status.Text = "Cancelling safely. Waiting for the current operation to release the profile…"; };
        Closing += (_, e) => { if (_operation != null) { e.Cancel = true; _status.Text = "Use Cancel and wait for the operation to finish before closing."; } };
        var originalConfiguration = System.Text.Json.JsonSerializer.Serialize(job, JsonFiles.Options);
        BuildConfiguration();
        _needsInitialSave = !File.Exists(_jobPath) || originalConfiguration != System.Text.Json.JsonSerializer.Serialize(job, JsonFiles.Options);
        _savedConfiguration = ConfigurationSnapshot();
        UpdateSaveState();
        _status.Text = "Configuration: " + _jobPath;
    }
    private static List<string> Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    private StackPanel Tab(string title)
    {
        var panel = new StackPanel { Margin = new Thickness(10) };
        _tabs.Items.Add(new TabItem { Header = title, Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel } });
        return panel;
    }
    private static void Info(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12), Foreground = Brushes.DimGray });
    private TextBox Text(Panel panel, string label, Func<string> get, Action<string> set, bool multiline = false)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
        var box = new TextBox { Text = get(), AcceptsReturn = multiline, MinHeight = multiline ? 62 : 26, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
        Help(box, label); panel.Children.Add(box); _commit.Add(() => set(box.Text.Trim()));
        _configurationValues.Add(() => box.Text.Trim());
        box.TextChanged += (_, _) => UpdateSaveState();
        return box;
    }
    private TextBox Folder(Panel panel, string label, Func<string> get, Action<string> set, Action? credentials = null, Action? testAccess = null)
    {
        var box = Text(panel, label, get, set);
        panel.Children.Remove(box);
        var row = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Button(buttons, "Browse…", () => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) { box.Text = dialog.FolderName; box.Focus(); } });
        if (credentials != null) Button(buttons, "Credentials…", credentials);
        if (testAccess != null) Button(buttons, "Test access", testAccess);
        DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons); row.Children.Add(box); panel.Children.Add(row);
        return box;
    }
    private void Number(Panel panel, string label, Func<string> get, Action<string> set) => Text(panel, label, get, set);
    private CheckBox Check(Panel panel, string label, Func<bool> get, Action<bool> set)
    {
        var check = new CheckBox { Content = label, IsChecked = get(), Margin = new Thickness(0, 10, 0, 4) };
        Help(check, label); panel.Children.Add(check); _commit.Add(() => set(check.IsChecked == true));
        _configurationValues.Add(() => check.IsChecked == true ? "true" : "false");
        check.Checked += (_, _) => UpdateSaveState(); check.Unchecked += (_, _) => UpdateSaveState();
        return check;
    }
    private void Choice(Panel panel, string label, IEnumerable<string> values, Func<string> get, Action<string> set)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
        var combo = new ComboBox { ItemsSource = values.ToArray(), SelectedItem = get(), MinHeight = 26 }; Help(combo, label); panel.Children.Add(combo);
        _commit.Add(() => set(combo.SelectedItem?.ToString() ?? ""));
        _configurationValues.Add(() => combo.SelectedItem?.ToString() ?? "");
        combo.SelectionChanged += (_, _) => UpdateSaveState();
    }
    private void ScheduleFields(Panel panel, string label, Schedule schedule, bool includeEnabled = true)
    {
        if (includeEnabled) Check(panel, label + " enabled", () => schedule.Enabled, x => schedule.Enabled = x);
        Choice(panel, "Frequency", ["Daily", "Weekly", "Monthly"], () => schedule.Frequency, x => schedule.Frequency = x);
        Text(panel, "Start time (local, HH:mm)", () => schedule.Time, x => schedule.Time = x);
        Choice(panel, "Day of week", Enum.GetNames<DayOfWeek>(), () => schedule.Day.ToString(), x => schedule.Day = Enum.Parse<DayOfWeek>(x));
        Number(panel, "Day of month (clamped to last day)", () => schedule.DayOfMonth.ToString(), x => schedule.DayOfMonth = int.Parse(x));
        Check(panel, "Run missed execution when available", () => schedule.CatchUp, x => schedule.CatchUp = x);
    }
    private Button Button(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 5, 2, 5), Padding = new Thickness(8, 4, 8, 4) };
        Help(button, label); button.Click += (_, _) => Guard(action); panel.Children.Add(button); return button;
    }
    private void ActionButton(string label, Action action) => Button(_actions, label, action);
    private void Guard(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Dinamica Backup Agent", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private void Commit() { foreach (var action in _commit) action(); }
    private string ConfigurationSnapshot() => System.Text.Json.JsonSerializer.Serialize(
        _configurationValues.Select(get => get()).Concat(new[] { _job.NasShare, _job.NasCredential, _job.AttachmentNasShare, _job.AttachmentNasCredential }));
    private void UpdateSaveState()
    {
        if (_saveButton == null || _savedConfiguration == null) return;
        _saveButton.IsEnabled = _needsInitialSave || ConfigurationSnapshot() != _savedConfiguration;
    }
    private void Save()
    {
        Commit(); SafePaths.Validate(_job, _job.BackupSchedule.Enabled || _job.TestSchedule.Enabled);
        JsonFiles.Write(_jobPath, _job);
        _savedConfiguration = ConfigurationSnapshot(); _needsInitialSave = false; UpdateSaveState();
        _status.Text = "Configuration saved at " + DateTime.Now.ToString("T") + ". Windows task registration is a separate action.";
    }
    private void RefreshHistory()
    {
        Commit(); SafePaths.Validate(_job, false);
        var localOnly = string.IsNullOrWhiteSpace(_job.Destination);
        try
        {
            _history.ItemsSource = Catalog.ReadRecentReceipts(_job, 100, !localOnly);
            _status.Text = "History refreshed using the current Windows access (read-only).";
        }
        catch (Exception ex) when (!localOnly && ex is IOException or UnauthorizedAccessException)
        {
            _history.ItemsSource = Catalog.ReadRecentReceipts(_job, 100, false);
            _status.Text = "NAS history is unavailable with the current Windows access; showing local history only. " + ex.Message;
        }
    }
    private void StoreCredential(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Enter a credential name first.");
        CredentialsDialog("Save protected credential: " + name, (user, password) => Secrets.Save(name, user, password));
    }
    private void RegisterTask()
    {
        EnsureSupportedApplication();
        Save();
        CredentialsDialog("Windows task identity (blank = current interactive user)", (user, password) =>
        {
            WindowsScheduler.Register(_jobPath, _job.Id, user, password);
            _status.Text = "Windows task registered successfully. " + (string.IsNullOrWhiteSpace(user) ? "Runs while your Windows session is signed in, including when locked." : "Runs with the supplied Windows account, including when signed out.");
            MessageBox.Show(this, _status.Text, "Windows task registered", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }
    private void CredentialsDialog(string title, Action<string, string> save)
    {
        var window = new Window { Title = title, Owner = this, Width = 550, Height = 310, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(15) }; window.Content = panel;
        if (title.StartsWith("Windows task", StringComparison.Ordinal))
            Info(panel, "Current Windows account: " + System.Security.Principal.WindowsIdentity.GetCurrent().Name + ". Bare usernames are resolved on this computer. Leave both fields blank to use the current session. Use the account password, not a Windows Hello PIN.");
        panel.Children.Add(new TextBlock { Text = "Username" }); var user = new TextBox { Margin = new Thickness(0, 5, 0, 10), ToolTip = "Username for this credential. For Windows task registration, leave blank to use the current interactive user." }; panel.Children.Add(user);
        panel.Children.Add(new TextBlock { Text = "Password" }); var password = new PasswordBox { Margin = new Thickness(0, 5, 0, 10), ToolTip = "Password stored securely or passed to Windows Task Scheduler; never written to the backup configuration." }; panel.Children.Add(password);
        var button = new Button { Content = "Save", Width = 100, ToolTip = "Save the protected credential or register the task with this identity." }; panel.Children.Add(button);
        button.Click += (_, _) => { try { save(user.Text, password.Password); password.Clear(); window.DialogResult = true; } catch (Exception ex) { MessageBox.Show(window, ex.Message); } };
        window.ShowDialog();
    }
    private async void Start(string kind, string? archive = null, string? destination = null, RestoreMode mode = RestoreMode.Database, IReadOnlyCollection<string>? selected = null, bool profileFiles = false)
    {
        if (_operation != null) return;
        try
        {
            EnsureSupportedApplication();
            Save();
            _operation = new CancellationTokenSource(); _tabs.IsEnabled = false; _actions.IsEnabled = false; _cancel.IsEnabled = true;
            var progress = new Progress<ProgressInfo>(p => { _status.Text = p.Stage; _progress.IsIndeterminate = p.Total == 0; _progress.Maximum = Math.Max(1, p.Total); _progress.Value = p.Completed; });
            var runner = Program.CreateRunner(_job, progress);
            var result = await Task.Run(() => runner.RunAsync(kind, _operation.Token, archive, destination, mode, selected, profileFiles));
            _status.Text = result.Status + ": " + result.Detail + (result.Warnings.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, result.Warnings.Take(8)));
            if (!string.IsNullOrEmpty(result.NotificationError)) _status.Text += "\nReport could not be sent: " + result.NotificationError;
        }
        catch (Exception ex) { _status.Text = "Failed: " + ex.Message; }
        finally { _operation?.Dispose(); _operation = null; _tabs.IsEnabled = true; _actions.IsEnabled = true; _cancel.IsEnabled = false; _progress.IsIndeterminate = false; }
    }
    private void ChooseRestore()
    {
        EnsureSupportedApplication();
        Save();
        var dialog = new OpenFileDialog { Filter = "Backup ZIP (*.zip)|*.zip", InitialDirectory = _job.Destination };
        if (dialog.ShowDialog(this) != true) return;
        var manifest = BackupEngine.ReadManifest(dialog.FileName);
        if (manifest.JobId != _job.Id) throw new InvalidDataException("The selected backup belongs to a different job.");
        var window = new Window { Title = "Restore backup", Owner = this, Width = 750, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(15) }; window.Content = panel;
        Info(panel, "Backup: " + manifest.CreatedUtc.ToLocalTime().ToString("g") + "\nSource: " + manifest.SourceProfile + "\nAttachments use the current mirror. Older files may no longer exist.");
        Info(panel, "Restoring the database makes EQM start offline for review. Restoring profile files can also restore an old Scheduled queue; review it before sending mail again.");
        var mode = new ComboBox { ItemsSource = Enum.GetValues<RestoreMode>(), SelectedItem = RestoreMode.Database, Margin = new Thickness(0, 5, 0, 10) }; panel.Children.Add(mode);
        var profile = new CheckBox { Content = "Also restore profile configuration files from ZIP", Margin = new Thickness(0, 5, 0, 10) }; panel.Children.Add(profile);
        panel.Children.Add(new TextBlock { Text = "Restore destination" }); var destination = new TextBox { Text = _job.ProfileDirectory, Margin = new Thickness(0, 5, 0, 10) }; panel.Children.Add(destination);
        var browse = new Button { Content = "Browse destination…", HorizontalAlignment = HorizontalAlignment.Left }; panel.Children.Add(browse);
        browse.Click += (_, _) => { var folder = new OpenFolderDialog(); if (folder.ShowDialog(window) == true) destination.Text = folder.FolderName; };
        var list = new ListBox { ItemsSource = manifest.Attachments.Select(x => x.Path), SelectionMode = SelectionMode.Extended, Height = 230, Margin = new Thickness(0, 10, 0, 10) }; panel.Children.Add(list);
        var restore = new Button { Content = "Review and restore…", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 6, 12, 6) }; panel.Children.Add(restore);
        restore.Click += (_, _) =>
        {
            var selectedMode = (RestoreMode)mode.SelectedItem;
            var paths = list.SelectedItems.Cast<string>().ToArray();
            if (selectedMode == RestoreMode.SelectedAttachments && paths.Length == 0) { MessageBox.Show(window, "Select at least one attachment."); return; }
            if (MessageBox.Show(window, "This will OVERWRITE matching live files.\n\nBackup: " + manifest.CreatedUtc.ToLocalTime().ToString("g") + "\nDestination: " + destination.Text + "\nMode: " + selectedMode + "\n\nPrevious replaced files will be retained in a recovery folder. Current attachments may differ from this historical database. Continue?", "Confirm restoration", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            window.Close(); Start("Restore", dialog.FileName, destination.Text, selectedMode, paths, profile.IsChecked == true);
        };
        window.ShowDialog();
    }
}
