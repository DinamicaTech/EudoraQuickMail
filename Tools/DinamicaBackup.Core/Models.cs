using System.Text.Json;
using System.Text.Json.Serialization;

namespace DinamicaBackup;

public sealed class BackupJob
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Eudora QuickMail";
    public string ApplicationType { get; set; } = "EQM";
    public string ApplicationDirectory { get; set; } = "";
    public string ProfileDirectory { get; set; } = "";
    public string DatabaseName { get; set; } = "mail.db";
    public List<string> ProfileFiles { get; set; } = [];
    public string ApplicationPath { get; set; } = "";
    public List<string> ApplicationArguments { get; set; } = [];
    public string Destination { get; set; } = "";
    public string AttachmentDestination { get; set; } = "";
    public List<string> AttachmentDirectories { get; set; } = ["Attachments"];
    public bool CopyAttachments { get; set; }
    public bool DeleteMissingAttachments { get; set; }
    public double MaximumDeletionPercent { get; set; } = 5;
    public int ShutdownTimeoutSeconds { get; set; } = 300;
    public Schedule BackupSchedule { get; set; } = new();
    public Schedule MaintenanceSchedule { get; set; } = new();
    public int MaintenanceMinutes { get; set; } = 120;
    public Schedule TestSchedule { get; set; } = new() { Frequency = "Weekly", Day = DayOfWeek.Sunday, Time = "04:00" };
    public string RestoreTestDirectory { get; set; } = "";
    public bool TestPreviousDay { get; set; } = true;
    public bool KeepSuccessfulTest { get; set; }
    public int AttachmentSampleSize { get; set; } = 100;
    public bool FullIntegrityCheck { get; set; }
    public int KeepDaily { get; set; } = 7;
    public int KeepWeekly { get; set; } = 4;
    public int KeepMonthly { get; set; } = 12;
    public List<SqlProbe> Probes { get; set; } = [];
    public string BeforeScript { get; set; } = "";
    public string AfterScript { get; set; } = "";
    public string FinallyScript { get; set; } = "";
    public int ScriptTimeoutSeconds { get; set; } = 300;
    public string NasShare { get; set; } = "";
    public string NasCredential { get; set; } = "";
    public string AttachmentNasShare { get; set; } = "";
    public string AttachmentNasCredential { get; set; } = "";
    public NotificationOptions Notification { get; set; } = new();
}
public sealed class Schedule
{
    public bool Enabled { get; set; }
    public string Frequency { get; set; } = "Daily";
    public string Time { get; set; } = "02:00";
    public DayOfWeek Day { get; set; } = DayOfWeek.Sunday;
    public int DayOfMonth { get; set; } = 1;
    public bool CatchUp { get; set; } = true;
    public DateTime? LatestOccurrence(DateTime localNow)
    {
        if (!Enabled) return null;
        if (!TimeOnly.TryParseExact(Time, "HH:mm", out var time)) throw new InvalidDataException("Invalid schedule time (HH:mm).");
        for (var i = 0; i < 63; i++)
        {
            var date = localNow.Date.AddDays(-i);
            if (Frequency == "Weekly" && date.DayOfWeek != Day) continue;
            if (Frequency == "Monthly" && date.Day != Math.Min(DayOfMonth, DateTime.DaysInMonth(date.Year, date.Month))) continue;
            var candidate = date.Add(time.ToTimeSpan());
            if (candidate <= localNow) return candidate;
        }
        return null;
    }
    public IReadOnlyList<DateTime> Pending(DateTime localNow, DateTime? lastHandled)
    {
        var pending = new List<DateTime>();
        var next = LatestOccurrence(localNow);
        while (next.HasValue && (!lastHandled.HasValue || next.Value > lastHandled.Value) && pending.Count < 31)
        {
            pending.Add(next.Value);
            if (!lastHandled.HasValue) break;
            next = LatestOccurrence(next.Value.AddTicks(-1));
        }
        pending.Reverse(); return pending;
    }
}
public sealed class NotificationOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool ImplicitTls { get; set; }
    public string Credential { get; set; } = "";
    public string From { get; set; } = "";
    public string SenderDisplayName { get; set; } = "";
    public string To { get; set; } = "";
}
public sealed record SqlProbe(string Name, string Sql);
public sealed record FileRecord(string Path, long Length, DateTime ModifiedUtc, string Sha256);
public sealed class Manifest
{
    public int FormatVersion { get; set; } = 1;
    public string JobId { get; set; } = "";
    public string BackupId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string DatabaseName { get; set; } = "";
    public string SourceProfile { get; set; } = "";
    public BackupJob? JobConfiguration { get; set; }
    public List<FileRecord> Files { get; set; } = [];
    public List<FileRecord> Attachments { get; set; } = [];
    public bool IncludesAttachments { get; set; }
    public long SchemaVersion { get; set; }
    public Dictionary<string, string?> ProbeValues { get; set; } = [];
    public List<SqlProbe> Probes { get; set; } = [];
}
public sealed class RunResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "Backup";
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }
    public string Status { get; set; } = "Running";
    public string Detail { get; set; } = "";
    public string Artifact { get; set; } = "";
    public int FilesCopied { get; set; }
    public List<string> Warnings { get; set; } = [];
    public string NotificationError { get; set; } = "";
    public string? CompletedMaintenanceOccurrence { get; set; }
}
public sealed record ProgressInfo(string Stage, long Completed = 0, long Total = 0);
public sealed record MaintenanceRequest(string Id, DateTimeOffset ExpiresUtc);
public sealed record MaintenanceResponse(string Id, int ProcessId, long StartUtcTicks);
public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException(path);
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
