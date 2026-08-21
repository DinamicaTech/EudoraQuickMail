using System.Text.Json;

namespace QuickMail.Services;

public sealed record EudoraImportMetadata(string SourceRoot, string AttachmentMode,
    DateTimeOffset ImportedAt)
{
    private const string FileName = "eudora-import-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static EudoraImportMetadata? Load(string profileDirectory)
    {
        var path = Path.Combine(profileDirectory, FileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<EudoraImportMetadata>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    public static void Save(string profileDirectory, string sourceRoot, string attachmentMode)
    {
        Directory.CreateDirectory(profileDirectory);
        var path = Path.Combine(profileDirectory, FileName);
        var temporary = path + ".writing";
        var value = new EudoraImportMetadata(Path.GetFullPath(sourceRoot), attachmentMode,
            DateTimeOffset.Now);
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, true);
    }
}
