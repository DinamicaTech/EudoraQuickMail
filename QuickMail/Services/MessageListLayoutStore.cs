using System.IO;
using System.Text.Json;
using QuickMail.Helpers;

namespace QuickMail.Services;

/// <summary>
/// Persists the visual message-list layout per physical display resolution. A layout from (for
/// example) 2560x1440 is deliberately never applied on 1920x1080: an absent key means shipped
/// defaults, not "use the last monitor".
/// </summary>
public sealed class MessageListLayoutStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MessageListLayoutStore(ProfileContext profile) =>
        _filePath = Path.Combine(profile.ProfileDir, "message-list-layout.json");

    public bool HasStoredLayouts => File.Exists(_filePath);

    public MessageListDisplayLayout? Load(string resolutionKey)
    {
        if (!File.Exists(_filePath)) return null;
        try
        {
            var profiles = JsonSerializer.Deserialize<Dictionary<string, MessageListDisplayLayout>>(
                File.ReadAllText(_filePath), JsonOptions);
            return profiles?.GetValueOrDefault(resolutionKey);
        }
        catch (Exception ex)
        {
            LogService.Log($"MessageListLayoutStore: could not read {_filePath}", ex);
            return null;
        }
    }

    public void Save(string resolutionKey, MessageListDisplayLayout layout)
    {
        try
        {
            Dictionary<string, MessageListDisplayLayout> profiles = [];
            if (File.Exists(_filePath))
            {
                try
                {
                    profiles = JsonSerializer.Deserialize<Dictionary<string, MessageListDisplayLayout>>(
                        File.ReadAllText(_filePath), JsonOptions) ?? [];
                }
                catch (JsonException)
                {
                    // Replace a malformed preference file. It contains presentation only; mail and
                    // account data are unrelated and must never be touched here.
                }
            }

            profiles[resolutionKey] = layout;
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(profiles, JsonOptions));
        }
        catch (Exception ex)
        {
            // A display preference must not block shutdown or hide-to-tray.
            LogService.Log($"MessageListLayoutStore: could not save {_filePath}", ex);
        }
    }
}

public sealed class MessageListDisplayLayout
{
    public double ReadingPaneHeight { get; set; } = 300;
    public List<MessageListColumnLayout> Columns { get; set; } = [];
}

public sealed class MessageListColumnLayout
{
    public string Id { get; set; } = string.Empty;
    public double Width { get; set; }
}
