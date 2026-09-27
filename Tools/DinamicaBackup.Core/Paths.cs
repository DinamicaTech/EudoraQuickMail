using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DinamicaBackup;

public static class SafePaths
{
    public static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Within(string root, string candidate) => Full(candidate).StartsWith(Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static string Child(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Expected a relative path.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(root, path)) throw new InvalidDataException("Path escapes its directory: " + relative);
        NoLinks(path);
        return path;
    }
    public static void NoLinks(string path)
    {
        for (var current = Full(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Links/junctions are not supported in backup paths: " + current);
    }
    public static void Separate(string a, string b)
    {
        if (Full(a).Equals(Full(b), StringComparison.OrdinalIgnoreCase) || Within(a, b) || Within(b, a))
            throw new InvalidDataException("Source, destination and restore-test directories must not overlap.");
    }
    public static IEnumerable<string> Files(string root)
    {
        NoLinks(root);
        foreach (var file in Directory.EnumerateFiles(root)) { NoLinks(file); yield return file; }
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            NoLinks(dir);
            foreach (var file in Files(dir)) yield return file;
        }
    }
    public static void DeleteOwnedDirectory(string path, string marker)
    {
        NoLinks(path);
        var owner = Child(path, ".backup-owner");
        if (!File.Exists(owner) || File.ReadAllText(owner) != marker) throw new IOException("Refusing to remove an unowned directory.");
        var files = Files(path).ToArray();
        foreach (var file in files) File.Delete(file);
        DeleteEmptyDirectories(path);
    }
    private static void DeleteEmptyDirectories(string root)
    {
        foreach (var directory in Directory.GetDirectories(root)) DeleteEmptyDirectories(directory);
        Directory.Delete(root, false);
    }
    public static void CheckSpace(string target, long required)
    {
        if (OperatingSystem.IsWindows() && target.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var existing = Full(target);
            while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing) ?? throw new IOException("Backup share is unavailable.");
            if (!GetDiskFreeSpaceEx(existing, out var available, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot check free space on the backup share.");
            if (available < (ulong)Math.Max(0, required) + 256UL * 1024 * 1024) throw new IOException("Insufficient free space on the backup share.");
            return;
        }
        var drive = new DriveInfo(Path.GetPathRoot(Full(target))!);
        if (drive.AvailableFreeSpace < required + 256L * 1024 * 1024) throw new IOException("Insufficient free space for this operation.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
    public static void Validate(BackupJob job, bool requireDestination = true)
    {
        if (job.FormatVersion != 1 || !Guid.TryParse(job.Id, out _)) throw new InvalidDataException("Unknown backup job format or invalid ID.");
        if (string.IsNullOrWhiteSpace(job.ProfileDirectory) || (requireDestination && string.IsNullOrWhiteSpace(job.Destination))) throw new InvalidDataException("Profile and backup destination are required.");
        NoLinks(job.ProfileDirectory);
        if (!string.IsNullOrWhiteSpace(job.Destination)) { NoLinks(job.Destination); Separate(job.ProfileDirectory, job.Destination); }
        _ = Child(job.ProfileDirectory, job.DatabaseName);
        foreach (var item in job.ProfileFiles.Concat(job.AttachmentDirectories)) _ = Child(job.ProfileDirectory, item);
        if (requireDestination && job.CopyAttachments)
        {
            if (string.IsNullOrWhiteSpace(job.AttachmentDestination)) throw new InvalidDataException("Attachment destination is required.");
            NoLinks(job.AttachmentDestination);
            Separate(job.ProfileDirectory, job.AttachmentDestination);
            Separate(job.Destination, job.AttachmentDestination);
        }
        if (requireDestination && !string.IsNullOrWhiteSpace(job.RestoreTestDirectory))
        {
            NoLinks(job.RestoreTestDirectory);
            Separate(job.ProfileDirectory, job.RestoreTestDirectory);
            Separate(job.Destination, job.RestoreTestDirectory);
            if (job.CopyAttachments) Separate(job.AttachmentDestination, job.RestoreTestDirectory);
        }
        if (job.KeepDaily < 1 || job.KeepWeekly < 0 || job.KeepMonthly < 0 || job.AttachmentSampleSize < 0 || job.ShutdownTimeoutSeconds < 1 || job.MaintenanceMinutes < 1 || job.ScriptTimeoutSeconds < 1 || job.MaximumDeletionPercent < 0 || job.MaximumDeletionPercent > 100)
            throw new InvalidDataException("Invalid retention, timeout or sample settings.");
        foreach (var schedule in new[] { job.BackupSchedule, job.MaintenanceSchedule, job.TestSchedule })
            if (schedule.Frequency is not ("Daily" or "Weekly" or "Monthly") || schedule.DayOfMonth is < 1 or > 31 || !TimeOnly.TryParseExact(schedule.Time, "HH:mm", out _))
                throw new InvalidDataException("Invalid schedule.");
    }
}
