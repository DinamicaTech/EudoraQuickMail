using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickMail.Helpers;

/// <summary>
/// Normalizes user/import supplied folder paths without changing meaningful whitespace inside a
/// folder name. Folder paths are persisted with '/' separators; IMAP aliases may keep their real
/// server spelling separately in LocalFolderBinding_shadow.
/// </summary>
public static class FolderPathNormalizer
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return string.Join('/', Segments(path));
    }

    public static IReadOnlyList<string> Segments(string path) =>
        path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .ToArray();
}
