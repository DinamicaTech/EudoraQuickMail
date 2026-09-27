using System.Diagnostics;
using System.Net;
using System.Text;
using MailKit.Security;
using MimeKit;

namespace DinamicaBackup;

public sealed class Runner(BackupJob job, IProgress<ProgressInfo>? progress = null)
{
    public Func<string, (string User, string Password)>? ReadCredential { get; set; }
    public Action? ConnectDestination { get; set; }
    public Action? ReopenApplication { get; set; }

    public async Task<RunResult> RunAsync(string kind, CancellationToken ct, string? archive = null,
        string? restoreDestination = null, RestoreMode restoreMode = RestoreMode.Database,
        IReadOnlyCollection<string>? selected = null, bool restoreProfileFiles = false)
    {
        SafePaths.Validate(job, kind != "Maintenance");
        if (job.ApplicationType != "EQM") throw new NotSupportedException("ProConta requires an Access validator and safe shutdown integration before backup operations can be enabled.");
        using var agent = ProfileLease.LockAgent(job.ProfileDirectory);
        Catalog? catalog = null;
        var run = new RunResult { Kind = kind };
        FileStream? lease = null;
        FileStream? destinationLease = null;
        MaintenanceResponse? closed = null;
        var runScripts = kind == "Backup";
        try
        {
            if (kind != "Maintenance")
            {
                ConnectDestination?.Invoke(); Directory.CreateDirectory(job.Destination);
                var control = SafePaths.Child(job.Destination, ".dinamica-backup/" + job.Id);
                Directory.CreateDirectory(control);
                destinationLease = new FileStream(Path.Combine(control, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catalog = new Catalog(job, kind == "Maintenance" ? Path.Combine(job.ProfileDirectory, ".backup-history-index", job.Id) : null);
            catalog.RecoverInterruptedRuns(); catalog.Save(run);
            if (kind != "Test")
            {
                if (kind == "Backup")
                {
                    var db = SafePaths.Child(job.ProfileDirectory, job.DatabaseName);
                    SafePaths.CheckSpace(job.Destination, new FileInfo(db).Length);
                    var probe = Path.Combine(job.Destination, ".write-probe-" + run.Id);
                    using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    File.Delete(probe);
                }
                (lease, closed) = await ProfileLease.AcquireAsync(job, progress, ct, response => closed = response);
            }
            var engine = new BackupEngine(job, progress);
            switch (kind)
            {
                case "Backup":
                    await Script(job.BeforeScript, "Before", ct);
                    await engine.BackupAsync(run, ct);
                    await Script(job.AfterScript, "After", ct);
                    break;
                case "Test":
                    archive ??= engine.Backups()
                        .Where(x => !job.TestPreviousDay || x.Manifest.CreatedUtc.LocalDateTime.Date == DateTime.Today.AddDays(-1))
                        .OrderByDescending(x => x.Manifest.CreatedUtc).FirstOrDefault().Path;
                    if (archive == null) throw new FileNotFoundException("No successful backup exists for the selected restore-test date.");
                    await new RestoreEngine(job, progress).TestAsync(archive, catalog, run, ct);
                    break;
                case "Restore":
                    if (archive == null || restoreDestination == null) throw new ArgumentException("Restore archive and destination required.");
                    // Never replace another live profile using only this job's source lock.
                    FileStream? otherLease = null;
                    try
                    {
                        if (!SafePaths.Full(restoreDestination).Equals(SafePaths.Full(job.ProfileDirectory), StringComparison.OrdinalIgnoreCase))
                        {
                            Directory.CreateDirectory(restoreDestination);
                            otherLease = new FileStream(Path.Combine(restoreDestination, ".backup-access.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        }
                        await new RestoreEngine(job, progress).RestoreAsync(archive, restoreDestination, restoreMode, selected ?? [], restoreProfileFiles, run, ct);
                    }
                    finally { otherLease?.Dispose(); }
                    break;
                case "Maintenance":
                    var until = MaintenanceEnd(DateTime.Now) ?? DateTime.Now.AddMinutes(job.MaintenanceMinutes);
                    await WaitUntil(until, ct);
                    run.Detail = "External maintenance interval completed; external backup success is not known.";
                    break;
                default: throw new ArgumentException("Unknown operation: " + kind);
            }
            if (kind is "Backup" or "Restore" && MaintenanceEnd(DateTime.Now) is { } end)
            {
                var occurrence = job.MaintenanceSchedule.LatestOccurrence(DateTime.Now)!.Value;
                await WaitUntil(end, ct);
                run.CompletedMaintenanceOccurrence = occurrence.ToString("O");
                catalog.Set("schedule-Maintenance", occurrence.ToString("O"));
                catalog.Save(new RunResult { Kind = "Maintenance", StartedUtc = new DateTimeOffset(occurrence), FinishedUtc = DateTimeOffset.UtcNow, Status = "Success", Detail = "External maintenance interval held after " + kind + "; external backup success is unknown." });
            }
            run.Status = run.Warnings.Count == 0 ? "Success" : "Warning";
        }
        catch (OperationCanceledException) { run.Status = "Cancelled"; run.Detail = "Cancelled. Incomplete copies are not published."; }
        catch (Microsoft.Data.Sqlite.SqliteException) when (ct.IsCancellationRequested) { run.Status = "Cancelled"; run.Detail = "SQLite validation cancelled."; }
        catch (Exception ex) { run.Status = "Failed"; run.Detail = ex.Message; }
        finally
        {
            if (runScripts && lease != null)
            {
                try { await Script(job.FinallyScript, "Finally", CancellationToken.None); }
                catch (Exception ex) { run.Warnings.Add("Final script: " + ex.Message); if (run.Status == "Success") run.Status = "Warning"; }
            }
            lease?.Dispose();
            if (closed != null)
            {
                try
                {
                    for (var attempt = 0; attempt < 20 && ProfileLease.IsAlive(closed); attempt++) await Task.Delay(250);
                    if (ProfileLease.IsAlive(closed)) throw new IOException("Application is still completing shutdown; reopen it manually after it exits.");
                    ReopenApplication?.Invoke();
                }
                catch (Exception ex) { run.Warnings.Add("Could not reopen application: " + ex.Message); if (run.Status == "Success") run.Status = "Warning"; }
            }
            run.FinishedUtc = DateTimeOffset.UtcNow;
            try { catalog?.Save(run); }
            catch (Exception ex) { run.Warnings.Add("Could not save destination history: " + ex.Message); if (run.Status == "Success") run.Status = "Warning"; }
            JsonFiles.Write(Path.Combine(job.ProfileDirectory, "backup-agent-last-run.json"), run);
            JsonFiles.Write(Path.Combine(job.ProfileDirectory, ".backup-history", job.Id, "run-" + run.Id + ".json"), run);
        }
        if (job.Notification.Enabled)
        {
            try { await Notify(run, catalog?.Recent() ?? [run], CancellationToken.None); }
            catch (Exception ex) { run.NotificationError = ex.Message; }
        }
        try { catalog?.Save(run); }
        catch (Exception ex) { run.Warnings.Add("Could not update report status in destination history: " + ex.Message); }
        finally { catalog?.Dispose(); destinationLease?.Dispose(); }
        JsonFiles.Write(Path.Combine(job.ProfileDirectory, "backup-agent-last-run.json"), run);
        JsonFiles.Write(Path.Combine(job.ProfileDirectory, ".backup-history", job.Id, "run-" + run.Id + ".json"), run);
        return run;
    }
    private DateTime? MaintenanceEnd(DateTime now)
    {
        var start = job.MaintenanceSchedule.LatestOccurrence(now);
        return start.HasValue && start.Value.AddMinutes(job.MaintenanceMinutes) > now ? start.Value.AddMinutes(job.MaintenanceMinutes) : null;
    }
    private async Task WaitUntil(DateTime end, CancellationToken ct)
    {
        while (DateTime.Now < end)
        {
            progress?.Report(new("Application closed for external maintenance until " + end.ToString("g")));
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }
    private async Task Script(string command, string phase, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        progress?.Report(new("Running " + phase + " script"));
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = job.ProfileDirectory };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; " + command)));
        start.Environment["DINAMICA_BACKUP_PROFILE"] = job.ProfileDirectory;
        start.Environment["DINAMICA_BACKUP_DESTINATION"] = job.Destination;
        using var process = Process.Start(start) ?? throw new IOException("Could not start script.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(job.ScriptTimeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); ct.ThrowIfCancellationRequested(); throw new TimeoutException(phase + " script exceeded timeout."); }
        if (process.ExitCode != 0) throw new IOException(phase + " script failed (exit " + process.ExitCode + ").");
    }
    public Task SendTestEmailAsync(CancellationToken ct) => Notify(new RunResult { Kind = "Email test", Status = "Success", FinishedUtc = DateTimeOffset.UtcNow, Detail = "Test email only. No backup or maintenance operation has been performed." }, [], ct);

    private async Task Notify(RunResult result, List<RunResult> recent, CancellationToken ct)
    {
        var n = job.Notification;
        var body = new StringBuilder("<html><body><h2>" + WebUtility.HtmlEncode(job.Name) + " — " + result.Status + "</h2>");
        body.Append("<p>Started: " + result.StartedUtc.ToLocalTime().ToString("g") + "<br>Duration: " + ReportFormatting.Duration(result.FinishedUtc - result.StartedUtc) + "<br>Files copied: " + result.FilesCopied + "</p><p>" + WebUtility.HtmlEncode(result.Detail) + "</p>");
        foreach (var warning in result.Warnings) body.Append("<p>" + WebUtility.HtmlEncode(warning) + "</p>");
        body.Append("<table><tr><th>Status</th><th>Operation</th><th>Started</th><th>Duration</th></tr>");
        foreach (var r in recent.Take(31))
        {
            var color = r.Status switch { "Success" => "green", "Warning" => "#c99a00", "Failed" => "red", _ => "gray" };
            body.Append($"<tr><td><span style='color:{color}'>■</span> {WebUtility.HtmlEncode(r.Status)}</td><td>{WebUtility.HtmlEncode(r.Kind)}</td><td>{r.StartedUtc.ToLocalTime():g}</td><td>{ReportFormatting.Duration(r.FinishedUtc - r.StartedUtc)}</td></tr>");
        }
        body.Append("</table></body></html>");
        var message = new MimeMessage(); message.From.Add(NotificationAddresses.Sender(n)); message.To.AddRange(NotificationAddresses.Recipients(n));
        message.Subject = job.Name + (result.Kind == "Email test" ? " — Test email" : " backup: " + result.Status); message.Body = new TextPart("html") { Text = body.ToString() };
        using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 30000 };
        await client.ConnectAsync(n.Host, n.Port, n.ImplicitTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
        if (!string.IsNullOrWhiteSpace(n.Credential)) { var secret = ReadCredential?.Invoke(n.Credential) ?? throw new IOException("SMTP credential unavailable."); await client.AuthenticateAsync(secret.User, secret.Password, ct); }
        await client.SendAsync(message, ct); await client.DisconnectAsync(true, ct);
    }
}
