namespace DinamicaBackup;

public sealed record DetectedApplication(string Directory, string Profile, string Executable);
public static class ApplicationDetection
{
    public static readonly string[] EqmProfileFiles = ["accounts.json", "config.ini", "hotkeys.json", "rules.json", "views.json", "contacts.json", "groups.json", "templates.json", "scheduled-mail.json", "scheduled-mail.json.forwarded.json", "snooze.json", "flags.json", "mail-activity.json", "eudora-import-settings.json"];
    public static List<SqlProbe> EqmProbes() => [new("Latest message date (UTC ticks)", "SELECT MAX(date_ticks) FROM MessageSummary")];
    public static DetectedApplication DetectEqm(string directory, BackupJob current)
    {
        directory = Path.GetFullPath(directory.Trim());
        if (!System.IO.Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var executable = Path.Combine(directory, "EudoraQM.exe");
        if (!File.Exists(executable)) executable = Path.Combine(directory, "QuickMail.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Choose the folder containing EudoraQM.exe or QuickMail.exe.");
        // A profile supplied by EQM itself is authoritative for custom --profileDir launches.
        string profile;
        if (!string.IsNullOrWhiteSpace(current.ApplicationPath) &&
            string.Equals(Path.GetDirectoryName(Path.GetFullPath(current.ApplicationPath)), directory, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(current.ProfileDirectory, "mail.db")))
            profile = current.ProfileDirectory;
        else
        {
            var pointer = Path.Combine(directory, "QuickMailDataPath.txt");
            if (File.Exists(pointer))
            {
                profile = File.ReadAllText(pointer).Trim();
                if (!Path.IsPathFullyQualified(profile)) throw new InvalidDataException("QuickMailDataPath.txt must contain an absolute profile path.");
            }
            else profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");
        }
        profile = Path.GetFullPath(profile);
        if (!File.Exists(Path.Combine(profile, "mail.db"))) throw new FileNotFoundException("No mail.db found in the detected profile: " + profile + ". Check the installation data path or use the advanced profile override.");
        return new(directory, profile, executable);
    }

    public static string UncShare(string destination)
    {
        if (!destination.StartsWith(@"\\", StringComparison.Ordinal)) return "";
        var parts = destination.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? @"\\" + parts[0] + @"\" + parts[1] : "";
    }
}
