namespace EudoraImporter;

internal sealed record EudoraMailbox(string FilePath, string RelativePath, string DisplayName);

internal static class EudoraMailboxDiscovery
{
    public static IEnumerable<EudoraMailbox> Find(string sourceDirectory)
    {
        var source = Path.GetFullPath(sourceDirectory);
        return Directory.EnumerateFiles(source, "*.mbx", SearchOption.AllDirectories)
            .Where(path => !IsExcluded(source, path))
            .Select(path => Create(source, path))
            .OrderBy(mailbox => mailbox.RelativePath, StringComparer.OrdinalIgnoreCase);
    }

    public static IEnumerable<EudoraMailbox> FindSelected(string sourceDirectory,
        IReadOnlyList<string> selectedSources, string destinationPath)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var target = NormalizeMailboxPath(destinationPath);
        var targetParent = target.Contains('/') ? target[..target.LastIndexOf('/')] : string.Empty;
        for (var index = 0; index < selectedSources.Count; index++)
        {
            var selected = Path.GetFullPath(selectedSources[index]);
            var selectedName = Path.GetFileNameWithoutExtension(selected.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var selectedTarget = index == 0 ? target
                : string.IsNullOrEmpty(targetParent) ? selectedName : targetParent + "/" + selectedName;

            if (File.Exists(selected) && selected.EndsWith(".mbx", StringComparison.OrdinalIgnoreCase))
            {
                yield return new EudoraMailbox(selected, Path.GetRelativePath(source, selected)
                    .Replace(Path.DirectorySeparatorChar, '/'), selectedTarget);
                continue;
            }
            if (!Directory.Exists(selected) || !selected.EndsWith(".fol", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var file in Directory.EnumerateFiles(selected, "*.mbx", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var relativeInside = Path.GetRelativePath(selected, file);
                var components = relativeInside.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Select(component => Path.GetFileNameWithoutExtension(component)).ToArray();
                var display = selectedTarget + "/" + string.Join('/', components);
                yield return new EudoraMailbox(file, Path.GetRelativePath(source, file)
                    .Replace(Path.DirectorySeparatorChar, '/'), display);
            }
        }
    }

    private static string NormalizeMailboxPath(string value) =>
        string.Join('/', value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static bool IsExcluded(string source, string path)
    {
        var relative = Path.GetRelativePath(source, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(component =>
                component.StartsWith("Attach", StringComparison.OrdinalIgnoreCase) ||
                component.StartsWith("Embedded", StringComparison.OrdinalIgnoreCase));
    }

    private static EudoraMailbox Create(string source, string path)
    {
        var relative = Path.GetRelativePath(source, path);
        var components = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Select(component => component.EndsWith(".fol", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(component)
                : component)
            .ToArray();
        components[^1] = Path.GetFileNameWithoutExtension(components[^1]);
        return new EudoraMailbox(path, relative.Replace(Path.DirectorySeparatorChar, '/'), string.Join('/', components));
    }
}
