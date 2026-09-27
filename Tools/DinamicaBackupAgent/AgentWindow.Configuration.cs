using System.IO;
using System.Windows;
using System.Windows.Controls;
using DinamicaBackup;

namespace DinamicaBackupAgent;

public sealed partial class AgentWindow
{
    private ComboBox _application = null!;
    private CheckBox _nativeEnabled = null!;
    private StackPanel _nativeSettings = null!;
    private TextBox _profileBox = null!, _databaseBox = null!, _executableBox = null!, _argumentsBox = null!, _filesBox = null!, _attachmentsBox = null!, _probesBox = null!;
    private string _lastDetectedFolder = "";
    private void BuildConfiguration()
    {
        var job = _job;
        var application = Tab("Application");
        application.Children.Add(new TextBlock { Text = "Application" });
        _application = new ComboBox { ItemsSource = new[] { "Eudora Quick Mail", "ProConta (not configured yet)" }, SelectedIndex = job.ApplicationType == "ProConta" ? 1 : 0, MinHeight = 28 };
        Help(_application, "Application"); application.Children.Add(_application);
        _commit.Add(() => job.ApplicationType = _application.SelectedIndex == 1 ? "ProConta" : "EQM");
        _configurationValues.Add(() => _application.SelectedIndex.ToString());
        _application.SelectionChanged += (_, _) => UpdateSaveState();
        var appFolder = Folder(application, "Eudora Quick Mail folder", () => string.IsNullOrWhiteSpace(job.ApplicationDirectory) ? Path.GetDirectoryName(job.ApplicationPath) ?? "" : job.ApplicationDirectory, x => job.ApplicationDirectory = x);
        var detected = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        Button(application, "Detect application settings", () =>
        {
            EnsureSupportedApplication(); DetectApplication(appFolder.Text); detected.Text = ApplicationSummary();
        });
        application.Children.Add(detected);
        detected.Text = ApplicationSummary();
        _application.SelectionChanged += (_, _) =>
        {
            detected.Text = _application.SelectedIndex == 0 ? ApplicationSummary() : "ProConta configuration will be added later. Backup and maintenance operations are not enabled for this application.";
            UpdateEnabledState();
        };
        var details = new StackPanel();
        application.Children.Add(new Expander { Header = "Detected settings / advanced overrides", Content = details, ToolTip = "Review detected values. Override only for a custom profile or installation.", Margin = new Thickness(0, 8, 0, 8) });
        _profileBox = Text(details, "Profile directory", () => job.ProfileDirectory, x => job.ProfileDirectory = x);
        _databaseBox = Text(details, "Database filename (relative to profile)", () => job.DatabaseName, x => job.DatabaseName = x);
        _executableBox = Text(details, "Application executable", () => job.ApplicationPath, x => job.ApplicationPath = x);
        _argumentsBox = Text(details, "Application arguments (one per line)", () => string.Join(Environment.NewLine, job.ApplicationArguments), x => job.ApplicationArguments = Lines(x), true);
        _filesBox = Text(details, "Profile files to include (relative paths, one per line)", () => string.Join(Environment.NewLine, job.ProfileFiles), x => job.ProfileFiles = Lines(x), true);
        _attachmentsBox = Text(details, "Attachment source directories (relative paths, one per line)", () => string.Join(Environment.NewLine, job.AttachmentDirectories), x => job.AttachmentDirectories = Lines(x), true);
        appFolder.LostKeyboardFocus += (_, _) => Guard(() =>
        {
            if (_application.SelectedIndex == 0 && Directory.Exists(appFolder.Text) && !string.Equals(appFolder.Text.Trim(), _lastDetectedFolder, StringComparison.OrdinalIgnoreCase))
            { DetectApplication(appFolder.Text); detected.Text = ApplicationSummary(); }
        });
        Button(application, "Register / Update Windows task…", RegisterTask);
        Info(application, "Saving does not register a Windows task. Register it explicitly after reviewing schedules. Stored credentials must be available to the Windows identity running the task.");

        var external = Tab("External Backup");
        Info(external, "Closes the application safely during this interval for an external backup program. It does not launch or verify that external backup.");
        ScheduleFields(external, "External backup", job.MaintenanceSchedule);
        Number(external, "External maintenance duration (minutes)", () => job.MaintenanceMinutes.ToString(), x => job.MaintenanceMinutes = int.Parse(x));
        Number(external, "Wait for application shutdown (seconds)", () => job.ShutdownTimeoutSeconds.ToString(), x => job.ShutdownTimeoutSeconds = int.Parse(x));
        Button(external, "Maintenance now", () => Start("Maintenance"));

        var native = Tab("Native Backup");
        _nativeEnabled = Check(native, "Native backup enabled", () => job.BackupSchedule.Enabled, x => job.BackupSchedule.Enabled = x);
        _nativeSettings = new StackPanel(); native.Children.Add(_nativeSettings);
        var general = _nativeSettings;
        Info(general, "Full independent ZIP backups of the database. Attachments are copied in native format without history. Currently the application remains closed throughout the backup.");
        Folder(general, "Database backup directory", () => job.Destination, x => job.Destination = x, () => EditNasCredentials(false));
        Check(general, "Copy attachments", () => job.CopyAttachments, x => job.CopyAttachments = x);
        Folder(general, "Attachment backup directory", () => job.AttachmentDestination, x => job.AttachmentDestination = x, () => EditNasCredentials(true));
        Check(general, "Delete destination attachments missing from source", () => job.DeleteMissingAttachments, x => job.DeleteMissingAttachments = x);
        Info(general, "Deleted or replaced attachments are unavailable to older backups. Linked files outside the profile (for example in Eudora) are not automatically included.");
        Number(general, "Suspend removals above this percentage", () => job.MaximumDeletionPercent.ToString(System.Globalization.CultureInfo.InvariantCulture), x => job.MaximumDeletionPercent = double.Parse(x, System.Globalization.CultureInfo.InvariantCulture));
        Number(general, "Keep daily copies", () => job.KeepDaily.ToString(), x => job.KeepDaily = int.Parse(x));
        Number(general, "Keep weekly copies", () => job.KeepWeekly.ToString(), x => job.KeepWeekly = int.Parse(x));
        Number(general, "Keep monthly copies", () => job.KeepMonthly.ToString(), x => job.KeepMonthly = int.Parse(x));
        ScheduleFields(general, "Native backup", job.BackupSchedule, false);
        Text(general, "Before-copy PowerShell command", () => job.BeforeScript, x => job.BeforeScript = x, true);
        Text(general, "After-success PowerShell command", () => job.AfterScript, x => job.AfterScript = x, true);
        Text(general, "Finally PowerShell command (also failure/cancel)", () => job.FinallyScript, x => job.FinallyScript = x, true);
        Number(general, "Script timeout (seconds)", () => job.ScriptTimeoutSeconds.ToString(), x => job.ScriptTimeoutSeconds = int.Parse(x));
        Button(general, "Back up now", () => Start("Backup"));
        _nativeEnabled.Checked += (_, _) => UpdateEnabledState();
        _nativeEnabled.Unchecked += (_, _) => UpdateEnabledState();

        var network = Tab("Notifications");
        Check(network, "Send email reports", () => job.Notification.Enabled, x => job.Notification.Enabled = x);
        Text(network, "SMTP host", () => job.Notification.Host, x => job.Notification.Host = x);
        Number(network, "SMTP port", () => job.Notification.Port.ToString(), x => job.Notification.Port = int.Parse(x));
        Check(network, "Implicit TLS (usually port 465); otherwise required STARTTLS", () => job.Notification.ImplicitTls, x => job.Notification.ImplicitTls = x);
        Text(network, "SMTP credential name", () => job.Notification.Credential, x => job.Notification.Credential = x);
        Button(network, "Save SMTP credentials…", () => { Commit(); StoreCredential(job.Notification.Credential); });
        NotificationAddresses.NormalizeLegacySender(job.Notification);
        Text(network, "Sender email", () => job.Notification.From, x => job.Notification.From = x);
        Text(network, "Sender display name (optional)", () => job.Notification.SenderDisplayName, x => job.Notification.SenderDisplayName = x);
        Text(network, "Report recipients", () => job.Notification.To, x => job.Notification.To = x);
        Button(network, "Test email", TestEmail);

        var tests = Tab("Restore test");
        ScheduleFields(tests, "Automated restore test", job.TestSchedule);
        Folder(tests, "Dedicated restore-test directory", () => job.RestoreTestDirectory, x => job.RestoreTestDirectory = x);
        Check(tests, "Test yesterday's latest successful backup", () => job.TestPreviousDay, x => job.TestPreviousDay = x);
        Check(tests, "Keep successful restored test files", () => job.KeepSuccessfulTest, x => job.KeepSuccessfulTest = x);
        Check(tests, "Full SQLite integrity check (slower)", () => job.FullIntegrityCheck, x => job.FullIntegrityCheck = x);
        Number(tests, "Number of attachment hashes to verify", () => job.AttachmentSampleSize.ToString(), x => job.AttachmentSampleSize = int.Parse(x));
        if (job.Probes.Count == 0 && job.ApplicationType == "EQM") job.Probes = ApplicationDetection.EqmProbes();
        _probesBox = Text(tests, "Validation queries: name | SELECT … (one per line, one scalar result)", () => string.Join(Environment.NewLine, job.Probes.Select(x => x.Name + " | " + x.Sql)), x => job.Probes = Lines(x).Select(line => { var parts = line.Split('|', 2); if (parts.Length != 2) throw new FormatException("Use name | SELECT …"); return new SqlProbe(parts[0].Trim(), parts[1].Trim()); }).ToList(), true);
        Info(tests, "Queries compare source values captured at backup with the restored copy. Message dates alone do not prove backup freshness. Failed test files are retained.");
        Button(tests, "Test restore now", () => Start("Test"));

        var history = new DockPanel { Margin = new Thickness(10) };
        var historyActions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(historyActions, Dock.Bottom); history.Children.Add(historyActions);
        Button(historyActions, "Refresh history", RefreshHistory);
        foreach (var (header, property) in new[] { ("Operation", "Kind"), ("Status", "Status"), ("Started (UTC)", "StartedUtc"), ("Finished (UTC)", "FinishedUtc"), ("Files", "FilesCopied"), ("Detail", "Detail") })
            _history.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(property), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _history.ToolTip = "Latest operations, status, UTC times, files copied and error details.";
        history.Children.Add(_history);
        _tabs.Items.Add(new TabItem { Header = "History", Content = history });
        _saveButton = Button(_actions, "Save configuration", Save);
        ActionButton("Restore…", ChooseRestore);
        Button(application, "Recover interrupted restoration…", () =>
        {
            EnsureSupportedApplication(); Commit(); var folder = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _job.ProfileDirectory };
            if (folder.ShowDialog(this) != true) return;
            if (MessageBox.Show(this, "Roll back the interrupted restoration in " + folder.FolderName + "? Previous files will be restored; partial restored files will be retained.", "Recover interrupted restoration", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            RestoreEngine.RecoverInterrupted(folder.FolderName, _job.Id); _status.Text = "Interrupted restoration rolled back.";
        });
        Help(_cancel, "Cancel / Reopen application");
        UpdateEnabledState();
    }
    private void UpdateEnabledState()
    {
        if (_nativeSettings != null) _nativeSettings.IsEnabled = _application.SelectedIndex == 0 && _nativeEnabled.IsChecked == true;
    }
    private void EnsureSupportedApplication()
    {
        if (_application.SelectedIndex != 0) throw new NotSupportedException("ProConta integration is not configured yet.");
    }
    private string ApplicationSummary() => "Profile: " + _job.ProfileDirectory + "\nDatabase: " + _job.DatabaseName + "\nExecutable: " + _job.ApplicationPath;
    private void DetectApplication(string folder)
    {
        var result = ApplicationDetection.DetectEqm(folder, _job);
        _job.ApplicationDirectory = result.Directory;
        _profileBox.Text = result.Profile;
        _databaseBox.Text = "mail.db";
        _executableBox.Text = result.Executable;
        _argumentsBox.Text = "--profileDir" + Environment.NewLine + result.Profile;
        _filesBox.Text = string.Join(Environment.NewLine, ApplicationDetection.EqmProfileFiles);
        _attachmentsBox.Text = "Attachments";
        // Existing custom queries are deliberately preserved.
        if (_job.Probes.Count == 0) _probesBox.Text = string.Join(Environment.NewLine, ApplicationDetection.EqmProbes().Select(x => x.Name + " | " + x.Sql));
        Commit();
        _lastDetectedFolder = folder.Trim();
    }
}
