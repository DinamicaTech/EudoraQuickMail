using System.IO;
using System.Text.Json;

namespace QuickMail.Helpers;

/// <summary>Never turn unreadable configuration into an empty configuration.</summary>
internal static class ProtectedJsonFile
{
    public static T Read<T>(string path, Func<T> missing, JsonSerializerOptions? options = null)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options)
                ?? throw new JsonException("The configuration contains null instead of the expected data.");
        }
        catch (FileNotFoundException) { return missing(); }
        catch (DirectoryNotFoundException) { return missing(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Cannot read configuration '{path}'. It has not been reset or overwritten. " +
                $"Restore a valid copy (the previous saved version, if available, is '{path}.previous').", ex);
        }
    }

    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        // Validate the existing file even if the caller has not loaded it this session.
        // Keep its last valid contents for explicit recovery; never silently restore stale rules/accounts.
        var previous = Read<T?>(path, () => default, options);
        var json = JsonSerializer.Serialize(value, options);
        if (previous is not null)
            AtomicFile.WriteAllText(path + ".previous", JsonSerializer.Serialize(previous, options));
        AtomicFile.WriteAllText(path, json);
    }
}
