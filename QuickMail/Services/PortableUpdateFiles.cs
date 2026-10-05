using System.IO;

namespace QuickMail.Services;

/// <summary>Shared by the app and its detached updater. Never installs profile/data files.</summary>
internal static class PortableUpdateFiles
{
    internal static string Relative(string value)
    {
        var path = value.Replace('\\', '/');
        var parts = path.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
            || p.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0))
            throw new InvalidDataException("Unsafe update path: " + value);
        var allowed = parts.Length > 1
            ? parts[0] is "Assets" or "BackupAgent" or "Updater"
            : path is "EudoraQM.exe" or "EudoraQM.pdb" or "DinamicaBackup.Core.pdb"
                or "Microsoft.Web.WebView2.Core.xml" or "Microsoft.Web.WebView2.WinForms.xml"
                or "Microsoft.Web.WebView2.Wpf.xml";
        if (!allowed) throw new InvalidDataException("Unexpected file in update: " + value);
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    internal static string SafePath(string root, string relative)
    {
        root = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(root, Relative(relative)));
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path escapes directory.");
        // Inspect all ancestors as well as the leaf; refuse junctions/symlinks, even in Data-free paths.
        for (var current = target; current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update cannot traverse a symbolic link: " + current);
        return target;
    }

    internal static void Apply(string stage, string target, string rollback, Action<string>? installing = null)
    {
        var files = Directory.GetFiles(stage, "*", SearchOption.AllDirectories)
            .Select(file => Relative(Path.GetRelativePath(stage, file))).ToArray();
        if (!files.Contains("EudoraQM.exe", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The update does not contain EudoraQM.exe.");
        if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw new InvalidDataException("Duplicate update paths.");
        var old = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Save every original before changing anything. A failed backup leaves the app intact.
        foreach (var relative in files)
        {
            var source = SafePath(stage, relative);
            var destination = SafePath(target, relative);
            var saved = SafePath(rollback, relative);
            if (!File.Exists(source)) throw new FileNotFoundException(source);
            if (!File.Exists(destination)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            File.Copy(destination, saved, overwrite: false);
            old.Add(relative);
        }
        var changed = new List<string>();
        try
        {
            foreach (var relative in files)
            {
                installing?.Invoke(relative);
                var destination = SafePath(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                changed.Add(relative); // includes a partially written file if Copy fails
                File.Copy(SafePath(stage, relative), destination, overwrite: true);
            }
        }
        catch (Exception original)
        {
            var failures = new List<Exception> { original };
            foreach (var relative in changed.AsEnumerable().Reverse())
            {
                try
                {
                    var destination = SafePath(target, relative);
                    if (old.Contains(relative)) File.Copy(SafePath(rollback, relative), destination, overwrite: true);
                    else if (File.Exists(destination)) File.Delete(destination);
                }
                catch (Exception ex) { failures.Add(ex); }
            }
            throw new AggregateException("Update failed. Previous files are retained in " + rollback, failures);
        }
    }
}
