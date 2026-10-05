using System.Text.Json;

namespace DinamicaBackup;

public sealed record LegacyAttachmentSource(string Root, IReadOnlyList<string> Directories)
{
    public const string BackupPrefix = "LegacyEudora/";

    public static LegacyAttachmentSource? Detect(string profileDirectory)
    {
        var metadata = Path.Combine(profileDirectory, "eudora-import-settings.json");
        if (!File.Exists(metadata)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(metadata));
        var root = document.RootElement;
        if (!root.TryGetProperty("AttachmentMode", out var mode) ||
            !string.Equals(mode.GetString(), "keep", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("SourceRoot", out var source)) return null;
        var path = source.GetString();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return null;
        SafePaths.NoLinks(path);
        var directories = Directory.EnumerateDirectories(path, "Attach*", SearchOption.TopDirectoryOnly)
            .Where(candidate => IsLegacyDirectory(Path.GetFileName(candidate)))
            .Select(candidate => { SafePaths.NoLinks(candidate); return candidate; })
            .Where(candidate => Directory.EnumerateFileSystemEntries(candidate).Any())
            .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase).ToArray();
        return directories.Length == 0 ? null : new(Path.GetFullPath(path), directories);
    }

    private static bool IsLegacyDirectory(string name) => name.Equals("Attach", StringComparison.OrdinalIgnoreCase)
        || (name.Length > 6 && name.StartsWith("Attach", StringComparison.OrdinalIgnoreCase) && name[6..].All(char.IsDigit));
}
