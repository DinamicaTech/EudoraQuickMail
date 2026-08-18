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
