using QuickMail.Services;
using Microsoft.Data.Sqlite;

namespace EudoraImporter;

internal static class LocalFolderTreeCommand
{
    public static int Run(string[] args)
    {
        var profile = Value(args, "--profile");
        if (string.IsNullOrWhiteSpace(profile))
        {
            Console.Error.WriteLine("Usage: EudoraImporter local-folder-tree --profile <folder> --apply-shadow|--remove-shadow");
            return 2;
        }

        profile = Path.GetFullPath(profile);
        var database = Path.Combine(profile, "mail.db");
        if (args.Contains("--report-shadow", StringComparer.OrdinalIgnoreCase))
            return Report(database);
        if (args.Contains("--remove-shadow", StringComparer.OrdinalIgnoreCase))
        {
            LocalFolderTreeMigrationService.RemoveShadowTree(database);
            Console.WriteLine("The shadow canonical-folder tables were removed. Legacy data was not changed.");
            return 0;
        }
        if (!args.Contains("--apply-shadow", StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Refusing to modify the database without --apply-shadow.");
            return 2;
        }

        var context = ProfileContext.TryCreate(profile, out var error);
        if (context == null)
        {
            Console.Error.WriteLine(error);
            return 2;
        }
        var accounts = new AccountService(context).LoadAccounts();
        Console.WriteLine($"Building the canonical local tree in: {database}");
        Console.WriteLine("Message, body, FTS and attachment tables will remain unchanged.");
        var result = new LocalFolderTreeMigrationService().BuildShadowTree(database, accounts);
        Console.WriteLine($"Legacy folder rows : {result.LegacyFolders:N0}");
        Console.WriteLine($"Canonical folders  : {result.CanonicalFolders:N0}");
        Console.WriteLine($"Legacy bindings    : {result.Bindings:N0}");
        Console.WriteLine($"Messages           : {result.Messages:N0}");
        Console.WriteLine($"Mapped exactly once: {result.MappedMessages:N0}");
        Console.WriteLine($"Unmapped           : {result.UnmappedMessages:N0}");
        Console.WriteLine($"Multiply mapped    : {result.MultiplyMappedMessages:N0}");
        return 0;
    }

    private static int Report(string database)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.canonical_path,COUNT(*) binding_count,
                   group_concat(b.legacy_full_name || ' [' || b.account_id || ']', ' | ')
              FROM LocalFolderNode_shadow n JOIN LocalFolderBinding_shadow b USING(folder_id)
             GROUP BY n.folder_id HAVING binding_count>1 ORDER BY n.canonical_path;
            """;
        Console.WriteLine("Merged legacy locations:");
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                Console.WriteLine($"  {reader.GetString(0)} ({reader.GetInt64(1)}): {reader.GetString(2)}");

        command.CommandText = """
            SELECT root.name,COUNT(child.folder_id),
                   COALESCE(group_concat(child.name, ', '),'')
              FROM LocalFolderNode_shadow root
              LEFT JOIN LocalFolderNode_shadow child ON child.parent_folder_id=root.folder_id
             WHERE root.parent_folder_id IS NULL GROUP BY root.folder_id ORDER BY root.name;
            """;
        Console.WriteLine("Canonical roots and direct children:");
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                Console.WriteLine($"  {reader.GetString(0)}: {reader.GetInt64(1)} children — {reader.GetString(2)}");
        return 0;
    }

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
