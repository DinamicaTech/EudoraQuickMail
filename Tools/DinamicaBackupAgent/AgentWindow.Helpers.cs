using System.Windows;
using System.Windows.Controls;
using DinamicaBackup;

namespace DinamicaBackupAgent;

public sealed partial class AgentWindow
{
    private void EditNasCredentials(bool attachments)
    {
        Commit();
        var existingShare = attachments ? _job.AttachmentNasShare : _job.NasShare;
        var existingName = attachments ? _job.AttachmentNasCredential : _job.NasCredential;
        var destination = attachments ? _job.AttachmentDestination : _job.Destination;
        var dialog = new Window { Owner = this, Title = attachments ? "Attachment destination credentials" : "Database destination credentials", Width = 560, Height = 430, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(16) }; dialog.Content = panel;
        Info(panel, "Optional for a NAS. Credentials are stored in Windows Credential Manager, not in the configuration file. Windows may reject different users for two shares on the same server; existing connections are never disconnected.");
        TextBox Field(string label, string initial, string help)
        {
            panel.Children.Add(new TextBlock { Text = label });
            var box = new TextBox { Text = initial, Margin = new Thickness(0, 3, 0, 8), ToolTip = help }; panel.Children.Add(box); return box;
        }
        var share = Field("UNC share", string.IsNullOrEmpty(existingShare) ? ApplicationDetection.UncShare(destination) : existingShare, @"Share root, for example \\nas\backup. Leave the credential name blank to use your Windows identity.");
        var name = Field("Credential name", string.IsNullOrEmpty(existingName) ? _job.Id + (attachments ? "-attachments" : "-database") : existingName, "Unique name for this protected credential. Clear it to use the current Windows identity.");
        var user = Field("Username", "", "NAS username, optionally DOMAIN\\user. Leave username and password empty to reuse the stored credential.");
        panel.Children.Add(new TextBlock { Text = "Password" });
        var password = new PasswordBox { ToolTip = "Stored securely for this Windows user. Never written to backup JSON.", Margin = new Thickness(0, 3, 0, 8) }; panel.Children.Add(password);
        var save = new Button { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 5, 12, 5), ToolTip = "Apply settings to this destination. Use Save configuration in the main window to persist the association." };
        panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            try
            {
                var credential = name.Text.Trim(); var unc = share.Text.Trim();
                if (credential.Length > 0)
                {
                    if (ApplicationDetection.UncShare(unc) != unc.TrimEnd('\\')) throw new ArgumentException("Enter a UNC share root, for example \\\\nas\\backup.");
                    if (user.Text.Length > 0) Secrets.Save(credential, user.Text.Trim(), password.Password);
                    else { if (password.Password.Length > 0) throw new ArgumentException("Enter a username with the password."); _ = Secrets.Read(credential); }
                }
                if (attachments) { _job.AttachmentNasShare = unc; _job.AttachmentNasCredential = credential; }
                else { _job.NasShare = unc; _job.NasCredential = credential; }
                UpdateSaveState();
                password.Clear(); dialog.DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Destination credentials", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        dialog.ShowDialog();
    }
    private async void TestEmail()
    {
        if (_operation != null) return;
        try
        {
            Commit();
            var n = _job.Notification;
            if (string.IsNullOrWhiteSpace(n.Host) || string.IsNullOrWhiteSpace(n.From) || string.IsNullOrWhiteSpace(n.To) || n.Port is < 1 or > 65535)
                throw new InvalidOperationException("Enter the SMTP host, a valid port, sender and recipients before testing.");
            _operation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _tabs.IsEnabled = false; _actions.IsEnabled = false; _cancel.IsEnabled = true; _progress.IsIndeterminate = true;
            _status.Text = "Sending test email… No backup is being performed.";
            await Program.CreateRunner(_job).SendTestEmailAsync(_operation.Token);
            _status.Text = "Test email accepted by the SMTP server for: " + n.To;
        }
        catch (OperationCanceledException) { _status.Text = "Email test cancelled or timed out."; }
        catch (Exception ex) { _status.Text = "Email test failed: " + ex.Message; }
        finally { _operation?.Dispose(); _operation = null; _tabs.IsEnabled = true; _actions.IsEnabled = true; _cancel.IsEnabled = false; _progress.IsIndeterminate = false; }
    }
    private static void Help(FrameworkElement control, string label)
    {
        var text = label switch
        {
            "Application" => "Choose the application to protect. EQM is supported; ProConta integration is pending.",
            "Eudora Quick Mail folder" => "Installation folder containing EudoraQM.exe. The profile is detected from the current EQM configuration or QuickMailDataPath.txt; it need not be inside this folder.",
            "Detect application settings" => "Read installation settings without opening or modifying the database. Populates profile, executable, arguments and standard source files. Custom validation queries are preserved.",
            "Native backup enabled" => "Enable scheduled native backups and unlock their settings. External maintenance and restore tests have independent switches. Register or update the Windows task to run schedules.",
            "Database backup directory" => "Destination for ZIP backups and their catalogue. Choose a local folder or UNC share outside the source profile and attachment destination.",
            "Attachment backup directory" => "Separate destination for a single native-file mirror. It must be empty for the first backup. This is not a historical archive.",
            "Browse…" => "Select a directory. This does not copy files or start a backup.",
            "Credentials…" => "Set optional NAS credentials for this destination only. Database and attachment destinations may use different credentials.",
            "Profile directory" => "Directory containing the live database and profile settings. Do not select the backup destination.",
            "Database filename (relative to profile)" => "Database path relative to the profile. EQM uses mail.db.",
            "Application executable" => "Full path of the executable to reopen after maintenance. It must belong to the selected installation.",
            "Application arguments (one per line)" => "Each line is one argument. For EQM use --profileDir on one line and the profile path on the next, without quotes.",
            "Profile files to include (relative paths, one per line)" => "Additional configuration files stored in the ZIP. Paths are relative to the profile; missing optional files are skipped.",
            "Attachment source directories (relative paths, one per line)" => "Source folders relative to the profile. External linked files, including Eudora attachments outside the profile, are not automatically included.",
            "Copy attachments" => "Copy new or changed attachment files and verify their hashes. Unchanged files are skipped using size and modification time.",
            "Delete destination attachments missing from source" => "Remove mirror files absent from the source. Older database backups may then reference unavailable attachments. Large removals are suspended.",
            "Suspend removals above this percentage" => "Safety threshold from 0 to 100. Deletions exceeding this proportion of the previous inventory are suspended and reported.",
            "Keep daily copies" => "Number of recent daily recovery points retained. Minimum 1; weekly and monthly selections may retain additional archives.",
            "Keep weekly copies" => "Number of weekly recovery points retained in addition to daily copies. Use 0 to disable this tier.",
            "Keep monthly copies" => "Number of monthly recovery points retained in addition to other tiers. Use 0 to disable this tier.",
            "Frequency" => "Run daily, on the selected weekday, or on the selected day of the month.",
            "Start time (local, HH:mm)" => "Local 24-hour time, for example 02:30. Windows Task Scheduler checks due operations every five minutes.",
            "Day of week" => "Used only for Weekly frequency.",
            "Day of month (clamped to last day)" => "Used only for Monthly frequency. Enter 1–31; shorter months use their last day.",
            "Run missed execution when available" => "Run an overdue scheduled operation when the agent becomes available. An expired external maintenance window is never replayed.",
            "External backup enabled" => "Enable the scheduled inactivity window independently of native backups. Configure the external backup program separately.",
            "External maintenance duration (minutes)" => "How long the application remains closed for external backup. The agent cannot detect whether the external backup has completed.",
            "Wait for application shutdown (seconds)" => "Maximum wait for safe shutdown and pending operations. The agent never forcibly kills the application.",
            "Before-copy PowerShell command" => "Runs after exclusive access is obtained and before copying. A nonzero exit code fails the operation. Do not embed passwords.",
            "After-success PowerShell command" => "Runs after successful backup work. Uses the Windows task identity. Do not embed passwords.",
            "Finally PowerShell command (also failure/cancel)" => "Cleanup command run after exclusive access was obtained, including failure or cancellation. Not run if safe shutdown failed.",
            "Script timeout (seconds)" => "Maximum duration for each PowerShell command. Scripts receive DINAMICA_BACKUP_PROFILE and DINAMICA_BACKUP_DESTINATION.",
            "Send email reports" => "Send operation summaries and recent history by SMTP. A mail failure is reported separately from backup success.",
            "SMTP host" => "Hostname of the outgoing mail server, without https:// or a port suffix.",
            "SMTP port" => "SMTP TCP port, usually 587 for STARTTLS or 465 for implicit TLS.",
            "Implicit TLS (usually port 465); otherwise required STARTTLS" => "Checked uses TLS from connection start; unchecked requires STARTTLS. Unencrypted fallback is not allowed.",
            "SMTP credential name" => "Name of a Windows Credential Manager entry. Leave blank only if the server allows unauthenticated delivery.",
            "Save SMTP credentials…" => "Store the SMTP username and password securely for the current Windows user.",
            "Sender email" => "Complete sender email address, for example user@example.com. It must be authorized by the SMTP account. Enter an alias in the separate display name field.",
            "Sender display name (optional)" => "Friendly name shown to recipients, for example Dinamica Backup. Leave blank to show only the email address.",
            "Report recipients" => "One or more email addresses separated by commas.",
            "Test email" => "Send a real test message using the current unsaved SMTP settings, even if reports are disabled. Does not run a backup or close EQM.",
            "Automated restore test enabled" => "Schedule verification of an existing backup in an isolated directory. It does not overwrite the live database.",
            "Dedicated restore-test directory" => "Separate folder for temporary test extraction, outside live data and both backup destinations.",
            "Test yesterday's latest successful backup" => "Select the previous day's latest successful backup. If none exists, report it instead of silently substituting another day.",
            "Keep successful restored test files" => "Keep extracted files after a successful test. Failed test files are always retained for investigation.",
            "Full SQLite integrity check (slower)" => "Run SQLite integrity_check instead of the faster check. Large databases may take substantially longer.",
            "Number of attachment hashes to verify" => "Number of sampled attachments to read and hash during a restore test. Use 0 to skip content sampling.",
            "Validation queries: name | SELECT … (one per line, one scalar result)" => "One read-only scalar SELECT per line, preceded by a name and |. Values captured at backup are compared after restoration. EQM supplies the latest message date in UTC ticks.",
            "Test restore now" => "Verify an existing backup in the dedicated test directory. Does not replace live data.",
            "Register / Update Windows task…" => "Save settings and explicitly register the five-minute scheduler. Optional Windows credentials are separate from NAS and SMTP credentials.",
            "Save configuration" => "Enabled only when settings need saving. After a successful save it becomes disabled. Saving does not start a backup or register a scheduled task.",
            "Back up now" => "Start a native backup now, requesting safe application shutdown first.",
            "Maintenance now" => "Start the external inactivity window now; this does not execute the external backup program.",
            "Restore…" => "Choose a backup and restoration scope. A separate confirmation warns before live files are replaced.",
            "Refresh history" => "Reload recent operation records. No backup is started.",
            "Recover interrupted restoration…" => "Roll back a previously interrupted restore using its recovery journal, after confirmation.",
            "Cancel / Reopen application" => "Request safe cancellation. Wait for cleanup and release of files before the application can reopen.",
            _ => "Review this setting before saving. Changes are not scheduled until the Windows task is registered."
        };
        control.ToolTip = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 };
        ToolTipService.SetShowOnDisabled(control, true);
        ToolTipService.SetShowDuration(control, 30000);
    }
}
