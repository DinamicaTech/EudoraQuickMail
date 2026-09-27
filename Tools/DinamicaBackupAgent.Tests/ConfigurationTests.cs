using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DinamicaBackup;
using DinamicaBackupAgent;

public sealed class ConfigurationTests
{
    [Fact] public Task SaveTracksEditsRevertsFailuresAndSuccessfulWrites() => InSta(() =>
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "backup-save-test-" + Guid.NewGuid().ToString("N"));
        var job = new BackupJob { ProfileDirectory = System.IO.Path.Combine(root, "profile"), Probes = ApplicationDetection.EqmProbes() };
        System.IO.Directory.CreateDirectory(root);
        System.IO.File.WriteAllText(System.IO.Path.Combine(root, ".backup-owner"), job.Id);
        var path = System.IO.Path.Combine(root, "job.json");
        JsonFiles.Write(path, job);
        var window = new AgentWindow(job, path);
        try
        {
            var save = Field<Button>(window, "_saveButton");
            var profile = Field<TextBox>(window, "_profileBox");
            Assert.False(save.IsEnabled);
            profile.Text += "-changed"; Assert.True(save.IsEnabled);
            profile.Text = job.ProfileDirectory; Assert.False(save.IsEnabled);
            profile.Text = ""; Assert.True(save.IsEnabled);
            var saveMethod = typeof(AgentWindow).GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Throws<TargetInvocationException>(() => saveMethod.Invoke(window, null));
            Assert.True(save.IsEnabled);
            profile.Text = System.IO.Path.Combine(root, "new-profile");
            saveMethod.Invoke(window, null);
            Assert.False(save.IsEnabled);
            Assert.Equal(profile.Text, JsonFiles.Read<BackupJob>(path).ProfileDirectory);
            job.AttachmentNasCredential = "new-destination-credential";
            typeof(AgentWindow).GetMethod("UpdateSaveState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Assert.True(save.IsEnabled);
            saveMethod.Invoke(window, null);
            Assert.False(save.IsEnabled);
        }
        finally { window.Close(); SafePaths.DeleteOwnedDirectory(root, job.Id); }
    });
    [Fact] public Task NewConfigurationNeedsInitialSave() => InSta(() =>
    {
        var window = new AgentWindow(new BackupJob(), System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));
        try { Assert.True(Field<Button>(window, "_saveButton").IsEnabled); }
        finally { window.Close(); }
    });
    private static T Field<T>(AgentWindow window, string name) => (T)typeof(AgentWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static Task InSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    [Fact] public Task TabOrderAndNativeToggle() => InSta(() =>
    {
        var window = new AgentWindow(new BackupJob(), System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unused-backup-ui-test.json"));
        try
        {
            var tabs = Field<TabControl>(window, "_tabs");
            Assert.Equal(new[] { "Application", "External Backup", "Native Backup", "Notifications", "Restore test", "History" }, tabs.Items.Cast<TabItem>().Select(x => (string)x.Header));
            var body = Field<StackPanel>(window, "_nativeSettings");
            var toggle = Field<CheckBox>(window, "_nativeEnabled");
            Assert.False(body.IsEnabled);
            toggle.IsChecked = true; Assert.True(body.IsEnabled);
            toggle.IsChecked = false; Assert.False(body.IsEnabled);
            Assert.NotNull(toggle.ToolTip);
        }
        finally { window.Close(); }
    });
    [Fact] public Task HistoryUsesAvailableHeight() => InSta(() =>
    {
        var window = new AgentWindow(new BackupJob(), System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unused-backup-ui-test.json"));
        try
        {
            var tabs = Field<TabControl>(window, "_tabs");
            var history = (TabItem)tabs.Items[5];
            var panel = Assert.IsType<DockPanel>(history.Content);
            var grid = Field<DataGrid>(window, "_history");
            Assert.Same(grid, panel.Children[panel.Children.Count - 1]);
            Assert.True(panel.LastChildFill); Assert.True(double.IsNaN(grid.Height));
            tabs.SelectedItem = history;
            // Lay out the tab content directly: a non-shown Window has no presentation source.
            panel.Measure(new Size(850, 600)); panel.Arrange(new Rect(0, 0, 850, 600)); panel.UpdateLayout();
            var largeHeight = grid.ActualHeight;
            Assert.True(largeHeight > 400);
            panel.Measure(new Size(850, 400)); panel.Arrange(new Rect(0, 0, 850, 400)); panel.UpdateLayout();
            Assert.True(grid.ActualHeight < largeHeight);
        }
        finally { window.Close(); }
    });
    [Fact] public Task PreserveCustomQueriesAndDisableProConta() => InSta(() =>
    {
        var job = new BackupJob { Probes = [new("Custom", "SELECT 1")] };
        var window = new AgentWindow(job, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unused-backup-ui-test.json"));
        try
        {
            Assert.Equal("Custom | SELECT 1", Field<TextBox>(window, "_probesBox").Text);
            Field<CheckBox>(window, "_nativeEnabled").IsChecked = true;
            Field<ComboBox>(window, "_application").SelectedIndex = 1;
            Assert.False(Field<StackPanel>(window, "_nativeSettings").IsEnabled);
        }
        finally { window.Close(); }
    });
    [Fact] public Task DefaultQueriesAndSeparatedDestinationButtons() => InSta(() =>
    {
        var job = new BackupJob();
        var window = new AgentWindow(job, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unused-backup-ui-test.json"));
        try
        {
            Assert.Contains("SELECT MAX(date_ticks) FROM MessageSummary", Field<TextBox>(window, "_probesBox").Text);
            var body = Field<StackPanel>(window, "_nativeSettings");
            var rows = body.Children.OfType<DockPanel>().ToArray();
            Assert.Equal(2, rows.Length);
            foreach (var row in rows)
            {
                var buttons = row.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
                Assert.Equal(new[] { "Browse…", "Credentials…" }, buttons.Select(x => (string)x.Content));
                Assert.All(buttons, button => Assert.NotNull(button.ToolTip));
            }
        }
        finally { window.Close(); }
    });
}
