using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Builds the account-independent local folder tree beside the legacy Folder catalogue.  This
/// first stage is intentionally non-destructive: message tables continue to use
/// (account_id,folder_name) until the generated map has been inspected and validated.
/// </summary>
public sealed class LocalFolderTreeMigrationService
{
    public sealed record Result(long LegacyFolders, long CanonicalFolders, long Bindings,
        long Messages, long MappedMessages, long UnmappedMessages, long MultiplyMappedMessages);

    private sealed record LegacyFolder(Guid AccountId, string FullName, string DisplayName,
        SpecialFolderKind Kind, bool IsContainer);

    public static bool HasShadowTree(string databasePath)
    {
        if (!File.Exists(databasePath)) return false;
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
             WHERE type='table' AND name IN ('LocalFolderNode_shadow','LocalFolderBinding_shadow');
            """;
        return Convert.ToInt32(command.ExecuteScalar()) == 2;
    }

    public static bool HasLegacyLocalFolderData(string databasePath, IReadOnlyCollection<AccountModel> accounts)
    {
        var ids = accounts.Where(account => account.BackendKind is BackendKind.Pop3Smtp or BackendKind.LocalArchive)
            .Select(account => account.Id.ToString("D")).ToList();
        if (ids.Count == 0 || !File.Exists(databasePath)) return false;
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        var parameters = string.Join(',', ids.Select((_, index) => "$a" + index));
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM Folder WHERE account_id IN ({parameters}) LIMIT 1);";
        for (var index = 0; index < ids.Count; index++) command.Parameters.AddWithValue("$a" + index, ids[index]);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    public Result BuildShadowTree(string databasePath, IReadOnlyCollection<AccountModel> accounts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath)) throw new FileNotFoundException("mail.db was not found.", databasePath);

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite;Pooling=False;");
        connection.Open();
        using var tx = connection.BeginTransaction();

        Execute(connection, tx, """
            DROP TABLE IF EXISTS LocalFolderBinding_shadow;
            DROP TABLE IF EXISTS LocalFolderNode_shadow;
            CREATE TABLE LocalFolderNode_shadow (
                folder_id       TEXT PRIMARY KEY,
                root_id         TEXT NOT NULL,
                parent_folder_id TEXT NULL,
                name            TEXT NOT NULL,
                canonical_path  TEXT NOT NULL,
                kind            INTEGER NOT NULL DEFAULT 0,
                is_container    INTEGER NOT NULL DEFAULT 0,
                UNIQUE(root_id, canonical_path)
            );
            CREATE TABLE LocalFolderBinding_shadow (
                account_id      TEXT NOT NULL,
                legacy_full_name TEXT NOT NULL,
                folder_id       TEXT NOT NULL,
                PRIMARY KEY(account_id, legacy_full_name),
                FOREIGN KEY(folder_id) REFERENCES LocalFolderNode_shadow(folder_id)
            );
            CREATE INDEX idx_local_folder_shadow_parent
                ON LocalFolderNode_shadow(root_id,parent_folder_id,name);
            CREATE INDEX idx_local_binding_shadow_folder
                ON LocalFolderBinding_shadow(folder_id);
            """);

        var accountMap = accounts.ToDictionary(a => a.Id);
        var localAccountIds = accounts
            .Where(account => account.BackendKind is BackendKind.Pop3Smtp or BackendKind.LocalArchive)
            .Select(account => account.Id).ToHashSet();
        var legacy = ReadLegacyFolders(connection, tx)
            .Where(folder => localAccountIds.Contains(folder.AccountId)).ToList();
        var nodes = new Dictionary<(Guid Root, string Path), (Guid Id, string Name, string Path, string? Parent,
            SpecialFolderKind Kind, bool Container)>();
        var bindings = new List<(Guid Account, string LegacyPath, Guid Folder)>();

        foreach (var folder in legacy)
        {
            var rootId = accountMap.TryGetValue(folder.AccountId, out var account)
                ? account.FolderTreeRootId ?? account.Id
                : folder.AccountId;
            var rootName = accountMap.Values
                .Where(candidate => (candidate.FolderTreeRootId ?? candidate.Id) == rootId)
                .Select(candidate => candidate.FolderTreeRootName)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? accountMap.GetValueOrDefault(rootId)?.AccountLabel
                ?? "Local mail";
            var rootKey = (rootId, string.Empty);
            if (!nodes.TryGetValue(rootKey, out var rootNode))
            {
                rootNode = (StableId(rootId, string.Empty), rootName, string.Empty, null,
                    SpecialFolderKind.None, true);
                nodes.Add(rootKey, rootNode);
            }
            var canonical = Canonicalize(folder.FullName, folder.Kind);
            var segments = canonical.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string path = string.Empty;
            Guid? parentId = rootNode.Id;
            for (var i = 0; i < segments.Length; i++)
            {
                path = path.Length == 0 ? segments[i] : path + "/" + segments[i];
                var key = (rootId, path.ToUpperInvariant());
                if (!nodes.TryGetValue(key, out var node))
                {
                    var id = StableId(rootId, path);
                    node = (id, segments[i], path, parentId?.ToString("D"),
                        i == segments.Length - 1 ? folder.Kind : SpecialFolderKind.None,
                        i < segments.Length - 1 || folder.IsContainer);
                    nodes.Add(key, node);
                }
                else if (i == segments.Length - 1)
                {
                    nodes[key] = (node.Id, node.Name, node.Path, node.Parent,
                        node.Kind == SpecialFolderKind.None ? folder.Kind : node.Kind,
                        node.Container || folder.IsContainer);
                }
                parentId = nodes[key].Id;
            }
            if (parentId.HasValue) bindings.Add((folder.AccountId, folder.FullName, parentId.Value));
        }

        using (var insertNode = connection.CreateCommand())
        {
            insertNode.Transaction = tx;
            insertNode.CommandText = """
                INSERT INTO LocalFolderNode_shadow
                    (folder_id,root_id,parent_folder_id,name,canonical_path,kind,is_container)
                VALUES($id,$root,$parent,$name,$path,$kind,$container);
                """;
            foreach (var pair in nodes)
            {
                var node = pair.Value;
                insertNode.Parameters.Clear();
                insertNode.Parameters.AddWithValue("$id", node.Id.ToString("D"));
                insertNode.Parameters.AddWithValue("$root", pair.Key.Root.ToString("D"));
                insertNode.Parameters.AddWithValue("$parent", (object?)node.Parent ?? DBNull.Value);
                insertNode.Parameters.AddWithValue("$name", node.Name);
                insertNode.Parameters.AddWithValue("$path", node.Path);
                insertNode.Parameters.AddWithValue("$kind", (int)node.Kind);
                insertNode.Parameters.AddWithValue("$container", node.Container ? 1 : 0);
                insertNode.ExecuteNonQuery();
            }
        }
        using (var insertBinding = connection.CreateCommand())
        {
            insertBinding.Transaction = tx;
            insertBinding.CommandText = """
                INSERT INTO LocalFolderBinding_shadow(account_id,legacy_full_name,folder_id)
                VALUES($account,$path,$folder);
                """;
            foreach (var binding in bindings)
            {
                insertBinding.Parameters.Clear();
                insertBinding.Parameters.AddWithValue("$account", binding.Account.ToString("D"));
                insertBinding.Parameters.AddWithValue("$path", binding.LegacyPath);
                insertBinding.Parameters.AddWithValue("$folder", binding.Folder.ToString("D"));
                insertBinding.ExecuteNonQuery();
            }
        }

        var result = Validate(connection, tx, localAccountIds);
        if (result.UnmappedMessages != 0 || result.MultiplyMappedMessages != 0 ||
            result.Messages != result.MappedMessages)
            throw new InvalidOperationException(
                $"Canonical folder validation failed: {result.UnmappedMessages:N0} unmapped and " +
                $"{result.MultiplyMappedMessages:N0} multiply mapped messages.");
        tx.Commit();
        return result;
    }

    public static void RemoveShadowTree(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite;Pooling=False;");
        connection.Open();
        Execute(connection, null,
            "DROP TABLE IF EXISTS LocalFolderBinding_shadow; DROP TABLE IF EXISTS LocalFolderNode_shadow;");
    }

    private static List<LegacyFolder> ReadLegacyFolders(SqliteConnection connection, SqliteTransaction tx)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        // Include paths that contain messages even if an older build omitted their Folder row.
        command.CommandText = """
            SELECT account_id,full_name,display_name,kind,is_container FROM Folder
            UNION
            SELECT account_id,folder_name,folder_name,0,0 FROM MessageSummary
             WHERE NOT EXISTS (SELECT 1 FROM Folder f
                 WHERE f.account_id=MessageSummary.account_id AND f.full_name=MessageSummary.folder_name)
            GROUP BY account_id,folder_name;
            """;
        var result = new List<LegacyFolder>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new LegacyFolder(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                reader.GetString(2), (SpecialFolderKind)reader.GetInt32(3), reader.GetInt32(4) != 0));
        return result;
    }

    private static Result Validate(SqliteConnection connection, SqliteTransaction tx,
        IReadOnlyCollection<Guid> localAccountIds)
    {
        long Scalar(string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar() ?? 0);
        }
        var legacyFolders = Scalar("SELECT COUNT(*) FROM Folder;");
        var canonical = Scalar("SELECT COUNT(*) FROM LocalFolderNode_shadow;");
        var bindings = Scalar("SELECT COUNT(*) FROM LocalFolderBinding_shadow;");
        var accountList = string.Join(',', localAccountIds.Select(id => $"'{id:D}'"));
        var localFilter = localAccountIds.Count == 0 ? "0" : $"account_id IN ({accountList})";
        var messages = Scalar($"SELECT COUNT(*) FROM MessageSummary WHERE {localFilter};");
        var mapped = Scalar("""
            SELECT COUNT(*) FROM MessageSummary m JOIN LocalFolderBinding_shadow b
              ON b.account_id=m.account_id AND b.legacy_full_name=m.folder_name;
            """);
        var unmapped = Scalar($"""
            SELECT COUNT(*) FROM MessageSummary m LEFT JOIN LocalFolderBinding_shadow b
              ON b.account_id=m.account_id AND b.legacy_full_name=m.folder_name
             WHERE b.folder_id IS NULL AND m.{localFilter};
            """);
        var multiplyMapped = Scalar("""
            SELECT COUNT(*) FROM (
              SELECT m.account_id,m.unique_id,m.folder_name,COUNT(*) n
                FROM MessageSummary m JOIN LocalFolderBinding_shadow b
                  ON b.account_id=m.account_id AND b.legacy_full_name=m.folder_name
               GROUP BY m.account_id,m.unique_id,m.folder_name HAVING n<>1);
            """);
        return new Result(legacyFolders, canonical, bindings, messages, mapped, unmapped, multiplyMapped);
    }

    private static string Canonicalize(string fullName, SpecialFolderKind kind)
    {
        var path = fullName.Replace('\\', '/').Trim('/');
        // Only system Inbox aliases collapse. Names such as _In remain ordinary, meaningful Eudora
        // containers and therefore retain both their underscore and their hierarchy.
        if (!path.Contains('/') && kind == SpecialFolderKind.Inbox &&
            (path.Equals("Inbox", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("In", StringComparison.OrdinalIgnoreCase)))
            return "In";
        return path;
    }

    private static Guid StableId(Guid rootId, string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rootId.ToString("D") + "\n" + path.ToUpperInvariant()));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
