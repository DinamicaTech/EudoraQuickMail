using System.Text;
using System.Text.Json;
using QuickMail.Models;

namespace EudoraImporter;

internal static class EudoraFilterImporter
{
    internal sealed record Result(int Imported, int Existing, int Unsupported, int Total);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Result Import(string filtersPath, string profilePath,
        IReadOnlyCollection<MailFolderModel> folders)
    {
        if (!File.Exists(filtersPath))
        {
            Console.WriteLine("      filters.pce was not found; no filters were imported.");
            return new Result(0, 0, 0, 0);
        }

        var rulesPath = Path.Combine(profilePath, "rules.json");
        var existing = LoadExisting(rulesPath);
        var keys = existing.Select(SemanticKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parsed = Parse(filtersPath, folders);
        var imported = 0;
        var duplicates = 0;
        foreach (var rule in parsed.Rules)
        {
            if (!keys.Add(SemanticKey(rule)))
            {
                duplicates++;
                continue;
            }
            existing.Add(rule);
            imported++;
        }

        if (imported > 0)
        {
            Directory.CreateDirectory(profilePath);
            var temporary = rulesPath + ".importing";
            File.WriteAllText(temporary, JsonSerializer.Serialize(existing, JsonOptions));
            File.Move(temporary, rulesPath, true);
        }
        return new Result(imported, duplicates, parsed.Unsupported, parsed.Total);
    }

    private static List<MailRule> LoadExisting(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<MailRule>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException ex)
        {
            throw new IOException($"The existing QuickMail rules file is invalid: {path}", ex);
        }
    }

    private sealed record Parsed(List<MailRule> Rules, int Unsupported, int Total);

    private static Parsed Parse(string path, IReadOnlyCollection<MailFolderModel> folders)
    {
        // filters.pce is an old ANSI file. Latin-1 preserves its byte values without requiring
        // a machine-wide code-page provider; Eudora field keywords and folder paths are ASCII.
        var lines = File.ReadAllLines(path, Encoding.Latin1);
        var blocks = new List<List<string>>();
        List<string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("rule ", StringComparison.OrdinalIgnoreCase))
            {
                current = [line];
                blocks.Add(current);
            }
            else current?.Add(line);
        }

        var rules = new List<MailRule>(blocks.Count);
        var unsupported = 0;
        foreach (var block in blocks)
        {
            if (!TryConvert(block, folders, out var rule)) unsupported++;
            else rules.Add(rule!);
        }
        return new Parsed(rules, unsupported, blocks.Count);
    }

    private static bool TryConvert(IReadOnlyList<string> block,
        IReadOnlyCollection<MailFolderModel> folders, out MailRule? rule)
    {
        rule = null;
        if (block.Any(line => line.StartsWith("forward ", StringComparison.OrdinalIgnoreCase) ||
                              line.StartsWith("junk", StringComparison.OrdinalIgnoreCase))) return false;
        var transfer = Value(block, "transfer ");
        if (string.IsNullOrWhiteSpace(transfer)) return false;

        var conjunction = Value(block, "conjunction ");
        if (!string.IsNullOrWhiteSpace(conjunction) &&
            !conjunction.Equals("ignore", StringComparison.OrdinalIgnoreCase)) return false;

        var headers = Values(block, "header ");
        var verbs = Values(block, "verb ");
        var values = Values(block, "value ");
        if (headers.Count == 0 || verbs.Count == 0 || values.Count == 0 ||
            !verbs[0].Equals("contains", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(values[0])) return false;

        var target = ResolveTargetFolder(transfer, folders);
        if (target is null) return false;
        var converted = new MailRule
        {
            Name = Value(block, "rule ") ?? "Imported Eudora filter",
            IsEnabled = true,
            ApplyAutomatically = block.Any(line => line.Equals("incoming", StringComparison.OrdinalIgnoreCase)),
            AccountId = null,
            UseFromCondition = false,
            AlsoMarkAsRead = false,
            Action = RuleAction.MoveToFolder,
            TargetFolder = target,
        };
        switch (headers[0].Trim().TrimEnd(':').ToLowerInvariant())
        {
            case "from": converted.UseFromCondition = true; converted.FromContains = values[0]; break;
            case "to": converted.UseToCondition = true; converted.ToContains = values[0]; break;
            case "subject": converted.UseSubjectCondition = true; converted.SubjectContains = values[0]; break;
            case "body": converted.UseBodyCondition = true; converted.BodyContains = values[0]; break;
            default: return false;
        }
        rule = converted;
        return true;
    }

    private static string? ResolveTargetFolder(string eudoraPath,
        IReadOnlyCollection<MailFolderModel> folders)
    {
        var parts = eudoraPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.EndsWith(".fol", StringComparison.OrdinalIgnoreCase) ||
                            part.EndsWith(".mbx", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(part) : part);
        var candidate = string.Join('/', parts);
        var folder = folders.FirstOrDefault(item =>
            item.FullName.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        if (folder is null) return null;
        if (!folder.IsContainer) return folder.FullName;
        return folders.FirstOrDefault(item => item.FullName.Equals(candidate + "/Messages",
            StringComparison.OrdinalIgnoreCase))?.FullName;
    }

    private static string? Value(IReadOnlyList<string> lines, string prefix)
    {
        var line = lines.FirstOrDefault(item =>
            item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line is null ? null : line[prefix.Length..].Trim();
    }

    private static List<string> Values(IReadOnlyList<string> lines, string prefix) =>
        lines.Where(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(line => line[prefix.Length..].Trim()).ToList();

    private static string SemanticKey(MailRule r) => string.Join('\u001f',
        r.Name, r.IsEnabled, r.ApplyAutomatically,
        r.UseFromCondition, r.FromContains, r.UseToCondition, r.ToContains,
        r.UseSubjectCondition, r.SubjectContains, r.UseBodyCondition, r.BodyContains,
        r.MustHaveAttachments, r.Action, r.AlsoMarkAsRead, r.TargetFolder);
}
