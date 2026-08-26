using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using QuickMail.Models;

namespace QuickMail.Services;

public partial class LocalStoreService : ILocalStoreService, ILocalMailboxStore
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public LocalStoreService(ProfileContext profile)
    {
        _dbPath = Path.Combine(profile.ProfileDir, "mail.db");
        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;";
    }

    public async Task<CanonicalLocalFolderTree?> LoadCanonicalLocalFolderTreeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('LocalFolderNode_shadow','LocalFolderBinding_shadow');";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync()) != 2) return null;

        var bindings = new Dictionary<Guid, List<CanonicalLocalFolderBinding>>();
        await using (var bindingCommand = connection.CreateCommand())
        {
            bindingCommand.CommandText = "SELECT folder_id,account_id,legacy_full_name FROM LocalFolderBinding_shadow;";
            await using var reader = await bindingCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var folderId = Guid.Parse(reader.GetString(0));
                if (!bindings.TryGetValue(folderId, out var list)) bindings[folderId] = list = [];
                list.Add(new CanonicalLocalFolderBinding(Guid.Parse(reader.GetString(1)), reader.GetString(2)));
            }
        }

        var folders = new List<CanonicalLocalFolder>();
        await using var command = connection.CreateCommand();
        // Folder already stores transactionally-maintained counters. Reading those cached values
        // keeps startup and ordinary tree refreshes proportional to the number of folders instead
        // of scanning/grouping the entire MessageSummary table (which can be tens of gigabytes).
        // Tools > Recalculate Folder Counts remains the authoritative repair path if required.
        command.CommandText = """
            SELECT n.folder_id,n.root_id,n.parent_folder_id,n.name,n.canonical_path,n.kind,
                   CASE WHEN n.is_container=1 OR EXISTS(
                       SELECT 1 FROM LocalFolderNode_shadow child WHERE child.parent_folder_id=n.folder_id)
                        THEN 1 ELSE 0 END,
                   COALESCE(SUM(f.unread_count),0),COALESCE(SUM(f.message_count),0)
              FROM LocalFolderNode_shadow n
              LEFT JOIN LocalFolderBinding_shadow b ON b.folder_id=n.folder_id
              LEFT JOIN Folder f ON f.account_id=b.account_id AND f.full_name=b.legacy_full_name
             GROUP BY n.folder_id ORDER BY n.canonical_path;
            """;
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
            {
                var id = Guid.Parse(reader.GetString(0));
                folders.Add(new CanonicalLocalFolder(id, Guid.Parse(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), reader.GetString(3),
                    reader.GetString(4), (SpecialFolderKind)reader.GetInt32(5), reader.GetInt32(6) != 0,
                    reader.GetInt32(7), reader.GetInt32(8),
                    bindings.GetValueOrDefault(id) ?? []));
            }
        return new CanonicalLocalFolderTree(folders);
    }

    public async Task<string> EnsureCanonicalFolderBindingAsync(Guid folderId, Guid accountId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var lookup = connection.CreateCommand();
        lookup.Transaction = (SqliteTransaction)tx;
        lookup.CommandText = """
            SELECT n.canonical_path,n.name,n.kind,n.is_container,b.legacy_full_name
              FROM LocalFolderNode_shadow n
              LEFT JOIN LocalFolderBinding_shadow b ON b.folder_id=n.folder_id AND b.account_id=$account
             WHERE n.folder_id=$folder;
            """;
        lookup.Parameters.AddWithValue("$folder", folderId.ToString("D"));
        lookup.Parameters.AddWithValue("$account", accountId.ToString("D"));
        string path;
        string name;
        int kind;
        int container;
        string? existingBinding;
        await using (var reader = await lookup.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new InvalidOperationException("The destination folder no longer exists.");
            path = reader.GetString(0);
            name = reader.GetString(1);
            kind = reader.GetInt32(2);
            container = reader.GetInt32(3);
            existingBinding = reader.IsDBNull(4) ? null : reader.GetString(4);
        }
        if (path.Length == 0) throw new InvalidOperationException("Messages cannot be stored directly in the tree root.");

        if (existingBinding != null)
        {
            // The canonical node is authoritative. A failed/aborted quick-filter creation could
            // previously turn an empty node into a leaf without updating its already-created
            // physical Folder row. MoveLocalMessagesAsync validates that physical row and then
            // rejected an otherwise valid drop as "container folder". Repair it whenever a binding
            // is resolved, which also heals profiles created by affected builds.
            await using var sync = connection.CreateCommand();
            sync.Transaction = (SqliteTransaction)tx;
            sync.CommandText = """
                UPDATE Folder SET display_name=$name,kind=$kind,is_container=$container
                 WHERE account_id=$account AND full_name=$path;
                """;
            sync.Parameters.AddWithValue("$name", name);
            sync.Parameters.AddWithValue("$kind", kind);
            sync.Parameters.AddWithValue("$container", container);
            sync.Parameters.AddWithValue("$account", accountId.ToString("D"));
            sync.Parameters.AddWithValue("$path", existingBinding);
            await sync.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            return existingBinding;
        }

        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? null : path[..slash];
        await using var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)tx;
        insert.CommandText = """
            INSERT OR IGNORE INTO Folder(account_id,full_name,display_name,parent_id,kind,
                exclude_from_all_mail,unread_count,message_count,sort_order,is_container)
            VALUES($account,$path,$name,$parent,$kind,0,0,0,0,$container);
            INSERT OR IGNORE INTO LocalFolderBinding_shadow(account_id,legacy_full_name,folder_id)
            VALUES($account,$path,$folder);
            """;
        insert.Parameters.AddWithValue("$account", accountId.ToString("D"));
        insert.Parameters.AddWithValue("$path", path);
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$parent", (object?)parent ?? DBNull.Value);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$container", container);
        insert.Parameters.AddWithValue("$folder", folderId.ToString("D"));
        await insert.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return path;
    }

    public async Task<Guid> CreateCanonicalFolderAsync(Guid parentFolderId, string name, Guid ownerAccountId,
        bool isContainer = true)
    {
        name = name.Trim();
        if (name.Length == 0 || name.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("Folder names cannot be empty or contain path separators.", nameof(name));
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var parent = connection.CreateCommand();
        parent.Transaction = tx;
        parent.CommandText = "SELECT root_id,canonical_path FROM LocalFolderNode_shadow WHERE folder_id=$id;";
        parent.Parameters.AddWithValue("$id", parentFolderId.ToString("D"));
        string root;
        string parentPath;
        await using (var reader = await parent.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new InvalidOperationException("The parent folder no longer exists.");
            root = reader.GetString(0);
            parentPath = reader.GetString(1);
        }
        var path = parentPath.Length == 0 ? name : parentPath + "/" + name;
        await using var existing = connection.CreateCommand();
        existing.Transaction = tx;
        existing.CommandText = "SELECT folder_id FROM LocalFolderNode_shadow WHERE root_id=$root AND canonical_path=$path COLLATE NOCASE;";
        existing.Parameters.AddWithValue("$root", root);
        existing.Parameters.AddWithValue("$path", path);
        var existingId = await existing.ExecuteScalarAsync();
        if (existingId is string value)
        {
            if (!isContainer)
            {
                // A previous failed Ctrl+Shift+Drop build could leave the canonical node behind as
                // an empty container before its physical binding was created. A retry with the same
                // name should finish the requested leaf, not keep returning an unusable container.
                await using var makeLeaf = connection.CreateCommand();
                makeLeaf.Transaction = tx;
                makeLeaf.CommandText = """
                    UPDATE LocalFolderNode_shadow SET is_container=0
                     WHERE folder_id=$id AND NOT EXISTS(
                           SELECT 1 FROM LocalFolderNode_shadow child
                            WHERE child.parent_folder_id=$id);
                    """;
                makeLeaf.Parameters.AddWithValue("$id", value);
                await makeLeaf.ExecuteNonQueryAsync();

                // Keep every existing physical binding in lockstep with the canonical node. This
                // is important when several POP/local accounts share one tree: the next message
                // dropped for any of them must see the same leaf/container classification.
                await using var syncBindings = connection.CreateCommand();
                syncBindings.Transaction = tx;
                syncBindings.CommandText = """
                    UPDATE Folder SET is_container=0
                     WHERE EXISTS(
                           SELECT 1 FROM LocalFolderBinding_shadow binding
                            JOIN LocalFolderNode_shadow node ON node.folder_id=binding.folder_id
                           WHERE binding.folder_id=$id
                             AND node.is_container=0
                             AND binding.account_id=Folder.account_id
                             AND binding.legacy_full_name=Folder.full_name);
                    """;
                syncBindings.Parameters.AddWithValue("$id", value);
                await syncBindings.ExecuteNonQueryAsync();
            }
            await MarkCanonicalContainerAsync(connection, tx, parentFolderId);
            await tx.CommitAsync();
            var parsed = Guid.Parse(value);
            await EnsureCanonicalFolderBindingAsync(parsed, ownerAccountId);
            return parsed;
        }
        var id = Guid.NewGuid();
        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO LocalFolderNode_shadow(folder_id,root_id,parent_folder_id,name,canonical_path,kind,is_container)
            VALUES($id,$root,$parent,$name,$path,0,$container);
            """;
        insert.Parameters.AddWithValue("$id", id.ToString("D"));
        insert.Parameters.AddWithValue("$root", root);
        insert.Parameters.AddWithValue("$parent", parentFolderId.ToString("D"));
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$path", path);
        insert.Parameters.AddWithValue("$container", isContainer ? 1 : 0);
        await insert.ExecuteNonQueryAsync();
        await MarkCanonicalContainerAsync(connection, tx, parentFolderId);
        await tx.CommitAsync();
        await EnsureCanonicalFolderBindingAsync(id, ownerAccountId);
        return id;
    }

    private static async Task MarkCanonicalContainerAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid folderId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE LocalFolderNode_shadow SET is_container=1 WHERE folder_id=$id;";
        command.Parameters.AddWithValue("$id", folderId.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<CanonicalFolderMoveResult> MoveCanonicalFolderAsync(Guid folderId, Guid newParentFolderId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var nodes = new Dictionary<Guid, (Guid Root, Guid? Parent, string Name, string Path)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT folder_id,root_id,parent_folder_id,name,canonical_path FROM LocalFolderNode_shadow;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = Guid.Parse(reader.GetString(0));
                nodes[id] = (Guid.Parse(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4));
            }
        }
        if (!nodes.TryGetValue(folderId, out var source) || !nodes.TryGetValue(newParentFolderId, out var parent))
            throw new InvalidOperationException("The source or destination folder no longer exists.");
        if (source.Parent == null) throw new InvalidOperationException("The tree root cannot be moved.");
        if (source.Root != parent.Root) throw new InvalidOperationException("Folders cannot be moved between different roots.");
        if (folderId == newParentFolderId || parent.Path.StartsWith(source.Path + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A folder cannot be moved into itself or one of its subfolders.");

        var newPath = parent.Path.Length == 0 ? source.Name : parent.Path + "/" + source.Name;
        if (string.Equals(source.Path, newPath, StringComparison.OrdinalIgnoreCase))
            return new CanonicalFolderMoveResult(source.Path, newPath, false);
        var collision = nodes.FirstOrDefault(pair => pair.Key != folderId && pair.Value.Root == source.Root &&
            pair.Value.Path.Equals(newPath, StringComparison.OrdinalIgnoreCase));
        if (collision.Key != Guid.Empty)
            return await MergeCanonicalFolderAsync(folderId, collision.Key, source.Path, newPath);

        var bindings = new List<(Guid Account, string CanonicalPath, string OldPath)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT b.account_id,n.canonical_path,b.legacy_full_name
                  FROM LocalFolderNode_shadow n JOIN LocalFolderBinding_shadow b ON b.folder_id=n.folder_id
                 WHERE n.root_id=$root AND (n.canonical_path=$old OR n.canonical_path LIKE $prefix ESCAPE '\')
                 ORDER BY length(n.canonical_path);
                """;
            command.Parameters.AddWithValue("$root", source.Root.ToString("D"));
            command.Parameters.AddWithValue("$old", source.Path);
            command.Parameters.AddWithValue("$prefix", source.Path.Replace("%", "\\%").Replace("_", "\\_") + "/%");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                bindings.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
        }

        // Rename the physical projections first. If one fails, canonical metadata remains unchanged
        // and the operation can safely be retried after the offending account is inspected.
        foreach (var binding in bindings.Where(candidate => !bindings.Any(ancestor =>
                     ancestor.Account == candidate.Account && ancestor.CanonicalPath.Length < candidate.CanonicalPath.Length &&
                     candidate.CanonicalPath.StartsWith(ancestor.CanonicalPath + "/", StringComparison.OrdinalIgnoreCase))))
            await RenameFolderPathAsync(binding.Account, binding.OldPath,
                newPath + binding.CanonicalPath[source.Path.Length..]);

        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var updateNodes = connection.CreateCommand();
        updateNodes.Transaction = tx;
        updateNodes.CommandText = """
            UPDATE LocalFolderNode_shadow SET
                canonical_path=$new || substr(canonical_path,length($old)+1),
                parent_folder_id=CASE WHEN folder_id=$id THEN $parent ELSE parent_folder_id END
            WHERE root_id=$root AND (canonical_path=$old OR canonical_path LIKE $prefix ESCAPE '\');
            """;
        updateNodes.Parameters.AddWithValue("$new", newPath);
        updateNodes.Parameters.AddWithValue("$old", source.Path);
        updateNodes.Parameters.AddWithValue("$id", folderId.ToString("D"));
        updateNodes.Parameters.AddWithValue("$parent", newParentFolderId.ToString("D"));
        updateNodes.Parameters.AddWithValue("$root", source.Root.ToString("D"));
        updateNodes.Parameters.AddWithValue("$prefix", source.Path.Replace("%", "\\%").Replace("_", "\\_") + "/%");
        await updateNodes.ExecuteNonQueryAsync();

        await using var markDestinationContainer = connection.CreateCommand();
        markDestinationContainer.Transaction = tx;
        markDestinationContainer.CommandText = "UPDATE LocalFolderNode_shadow SET is_container=1 WHERE folder_id=$parent;";
        markDestinationContainer.Parameters.AddWithValue("$parent", newParentFolderId.ToString("D"));
        await markDestinationContainer.ExecuteNonQueryAsync();

        // Do not derive this from legacy_full_name: canonical aliases deliberately allow a binding
        // such as canonical "In" -> physical "Inbox". Use the canonical node path captured above.
        await using var updateBinding = connection.CreateCommand();
        updateBinding.Transaction = tx;
        updateBinding.CommandText = """
            UPDATE LocalFolderBinding_shadow SET legacy_full_name=$new
             WHERE account_id=$account AND legacy_full_name=$old;
            """;
        var bindingNew = updateBinding.Parameters.Add("$new", SqliteType.Text);
        var bindingAccount = updateBinding.Parameters.Add("$account", SqliteType.Text);
        var bindingOld = updateBinding.Parameters.Add("$old", SqliteType.Text);
        foreach (var binding in bindings)
        {
            bindingNew.Value = newPath + binding.CanonicalPath[source.Path.Length..];
            bindingAccount.Value = binding.Account.ToString("D");
            bindingOld.Value = binding.OldPath;
            await updateBinding.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return new CanonicalFolderMoveResult(source.Path, newPath, false);
    }

    private async Task<CanonicalFolderMoveResult> MergeCanonicalFolderAsync(
        Guid sourceFolderId, Guid destinationFolderId, string oldPath, string newPath)
    {
        var tree = await LoadCanonicalLocalFolderTreeAsync()
            ?? throw new InvalidOperationException("The canonical folder tree is unavailable.");
        var sourceNodes = tree.Folders
            .Where(folder => folder.FolderId == sourceFolderId ||
                folder.CanonicalPath.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(folder => folder.CanonicalPath.Length)
            .ToList();
        if (sourceNodes.Count == 0) throw new InvalidOperationException("The source folder no longer exists.");
        var sourceIds = sourceNodes.Select(folder => folder.FolderId).ToHashSet();
        var destinationNodes = tree.Folders
            .Where(folder => !sourceIds.Contains(folder.FolderId))
            .ToDictionary(folder => folder.CanonicalPath, StringComparer.OrdinalIgnoreCase);
        if (!destinationNodes.TryGetValue(newPath, out var destinationRoot) ||
            destinationRoot.FolderId != destinationFolderId)
            throw new InvalidOperationException("The destination folder no longer exists.");

        var targetIds = new Dictionary<Guid, Guid>();
        foreach (var source in sourceNodes)
        {
            var targetPath = newPath + source.CanonicalPath[oldPath.Length..];
            targetIds[source.FolderId] = destinationNodes.TryGetValue(targetPath, out var existing)
                ? existing.FolderId : source.FolderId;
        }

        // A physical merge at an ancestor also moves all descendant paths. Record those mappings
        // so descendant shadow bindings can be rewritten without attempting the same move twice.
        var physicalMoves = new List<(Guid Account, string OldRoot, string NewRoot)>();
        foreach (var source in sourceNodes)
        {
            if (targetIds[source.FolderId] == source.FolderId) continue;
            var target = tree.Folders.Single(folder => folder.FolderId == targetIds[source.FolderId]);
            foreach (var binding in source.Bindings)
            {
                if (physicalMoves.Any(move => move.Account == binding.AccountId &&
                    (binding.LegacyFullName.Equals(move.OldRoot, StringComparison.OrdinalIgnoreCase) ||
                     binding.LegacyFullName.StartsWith(move.OldRoot + "/", StringComparison.OrdinalIgnoreCase))))
                    continue;
                var destinationBinding = target.Bindings.FirstOrDefault(candidate =>
                    candidate.AccountId == binding.AccountId);
                if (destinationBinding == null || destinationBinding.LegacyFullName.Equals(
                        binding.LegacyFullName, StringComparison.OrdinalIgnoreCase)) continue;
                await MergeFolderPathAsync(binding.AccountId, binding.LegacyFullName,
                    destinationBinding.LegacyFullName);
                physicalMoves.Add((binding.AccountId, binding.LegacyFullName,
                    destinationBinding.LegacyFullName));
            }
        }

        string RewrittenPhysicalPath(CanonicalLocalFolderBinding binding)
        {
            var move = physicalMoves
                .Where(candidate => candidate.Account == binding.AccountId &&
                    (binding.LegacyFullName.Equals(candidate.OldRoot, StringComparison.OrdinalIgnoreCase) ||
                     binding.LegacyFullName.StartsWith(candidate.OldRoot + "/", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(candidate => candidate.OldRoot.Length)
                .FirstOrDefault();
            return move.OldRoot == null
                ? binding.LegacyFullName
                : move.NewRoot + binding.LegacyFullName[move.OldRoot.Length..];
        }

        await using var connection = await OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        foreach (var source in sourceNodes)
        {
            var targetId = targetIds[source.FolderId];
            var targetPath = newPath + source.CanonicalPath[oldPath.Length..];
            var mappedParentId = source.FolderId == sourceFolderId
                ? destinationRoot.ParentFolderId
                : source.ParentFolderId is { } parentId && targetIds.TryGetValue(parentId, out var mapped)
                    ? mapped : source.ParentFolderId;

            foreach (var binding in source.Bindings)
            {
                var physicalPath = RewrittenPhysicalPath(binding);
                if (targetId != source.FolderId)
                {
                    await using var collision = connection.CreateCommand();
                    collision.Transaction = transaction;
                    collision.CommandText = """
                        DELETE FROM LocalFolderBinding_shadow
                         WHERE folder_id=$source AND account_id=$account AND legacy_full_name=$old
                           AND EXISTS(SELECT 1 FROM LocalFolderBinding_shadow
                                       WHERE folder_id=$target AND account_id=$account AND legacy_full_name=$new);
                        UPDATE LocalFolderBinding_shadow SET folder_id=$target,legacy_full_name=$new
                         WHERE folder_id=$source AND account_id=$account AND legacy_full_name=$old;
                        """;
                    collision.Parameters.AddWithValue("$source", source.FolderId.ToString("D"));
                    collision.Parameters.AddWithValue("$target", targetId.ToString("D"));
                    collision.Parameters.AddWithValue("$account", binding.AccountId.ToString("D"));
                    collision.Parameters.AddWithValue("$old", binding.LegacyFullName);
                    collision.Parameters.AddWithValue("$new", physicalPath);
                    await collision.ExecuteNonQueryAsync();
                }
                else if (!physicalPath.Equals(binding.LegacyFullName, StringComparison.OrdinalIgnoreCase))
                {
                    await using var updateBinding = connection.CreateCommand();
                    updateBinding.Transaction = transaction;
                    updateBinding.CommandText = """
                        UPDATE LocalFolderBinding_shadow SET legacy_full_name=$new
                         WHERE folder_id=$folder AND account_id=$account AND legacy_full_name=$old;
                        """;
                    updateBinding.Parameters.AddWithValue("$folder", source.FolderId.ToString("D"));
                    updateBinding.Parameters.AddWithValue("$account", binding.AccountId.ToString("D"));
                    updateBinding.Parameters.AddWithValue("$old", binding.LegacyFullName);
                    updateBinding.Parameters.AddWithValue("$new", physicalPath);
                    await updateBinding.ExecuteNonQueryAsync();
                }
            }

            if (targetId == source.FolderId)
            {
                await using var moveNode = connection.CreateCommand();
                moveNode.Transaction = transaction;
                moveNode.CommandText = """
                    UPDATE LocalFolderNode_shadow SET canonical_path=$path,parent_folder_id=$parent
                     WHERE folder_id=$folder;
                    """;
                moveNode.Parameters.AddWithValue("$path", targetPath);
                moveNode.Parameters.AddWithValue("$parent", (object?)mappedParentId?.ToString("D") ?? DBNull.Value);
                moveNode.Parameters.AddWithValue("$folder", source.FolderId.ToString("D"));
                await moveNode.ExecuteNonQueryAsync();
            }
        }

        // Colliding source nodes are now empty aliases. Repoint any remaining child references and
        // remove them deepest-first, retaining the pre-existing destination nodes.
        foreach (var source in sourceNodes.OrderByDescending(folder => folder.CanonicalPath.Length))
        {
            var targetId = targetIds[source.FolderId];
            if (targetId == source.FolderId) continue;
            await using var reparent = connection.CreateCommand();
            reparent.Transaction = transaction;
            reparent.CommandText = """
                UPDATE LocalFolderNode_shadow SET parent_folder_id=$target WHERE parent_folder_id=$source;
                DELETE FROM LocalFolderBinding_shadow WHERE folder_id=$source;
                DELETE FROM LocalFolderNode_shadow WHERE folder_id=$source;
                UPDATE LocalFolderNode_shadow SET is_container=1 WHERE folder_id=$target;
                """;
            reparent.Parameters.AddWithValue("$source", source.FolderId.ToString("D"));
            reparent.Parameters.AddWithValue("$target", targetId.ToString("D"));
            await reparent.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return new CanonicalFolderMoveResult(oldPath, newPath, true);
    }

    public async Task<CanonicalFolderMoveResult> RenameCanonicalFolderAsync(Guid folderId, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("Folder names cannot be empty or contain path separators.", nameof(newName));

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        Guid rootId;
        Guid? parentId;
        string oldName;
        string oldPath;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT root_id,parent_folder_id,name,canonical_path FROM LocalFolderNode_shadow WHERE folder_id=$id;";
            lookup.Parameters.AddWithValue("$id", folderId.ToString("D"));
            await using var reader = await lookup.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new InvalidOperationException("The folder no longer exists.");
            rootId = Guid.Parse(reader.GetString(0));
            parentId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));
            oldName = reader.GetString(2);
            oldPath = reader.GetString(3);
        }
        if (parentId == null) throw new InvalidOperationException("The tree root cannot be renamed here.");
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
            return new CanonicalFolderMoveResult(oldPath, oldPath, false);

        var slash = oldPath.LastIndexOf('/');
        var parentPath = slash < 0 ? string.Empty : oldPath[..slash];
        var newPath = parentPath.Length == 0 ? newName : parentPath + "/" + newName;
        await using (var collision = connection.CreateCommand())
        {
            collision.CommandText = "SELECT 1 FROM LocalFolderNode_shadow WHERE root_id=$root AND folder_id<>$id AND canonical_path=$path COLLATE NOCASE LIMIT 1;";
            collision.Parameters.AddWithValue("$root", rootId.ToString("D"));
            collision.Parameters.AddWithValue("$id", folderId.ToString("D"));
            collision.Parameters.AddWithValue("$path", newPath);
            if (await collision.ExecuteScalarAsync() != null)
                throw new InvalidOperationException($"A folder named '{newName}' already exists here.");
        }

        var bindings = new List<(Guid Account, string CanonicalPath, string OldPath)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT b.account_id,n.canonical_path,b.legacy_full_name
                  FROM LocalFolderNode_shadow n JOIN LocalFolderBinding_shadow b ON b.folder_id=n.folder_id
                 WHERE n.root_id=$root AND (n.canonical_path=$old OR n.canonical_path LIKE $prefix ESCAPE '\')
                 ORDER BY length(n.canonical_path);
                """;
            command.Parameters.AddWithValue("$root", rootId.ToString("D"));
            command.Parameters.AddWithValue("$old", oldPath);
            command.Parameters.AddWithValue("$prefix", oldPath.Replace("%", "\\%").Replace("_", "\\_") + "/%");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                bindings.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
        }

        string PhysicalNewPath((Guid Account, string CanonicalPath, string OldPath) binding)
        {
            var suffix = binding.CanonicalPath[oldPath.Length..];
            var oldPhysicalRoot = suffix.Length > 0 && binding.OldPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? binding.OldPath[..^suffix.Length]
                : binding.OldPath;
            var cut = Math.Max(oldPhysicalRoot.LastIndexOf('/'), oldPhysicalRoot.LastIndexOf('\\'));
            var newPhysicalRoot = cut < 0
                ? newName
                : oldPhysicalRoot[..(cut + 1)] + newName;
            return newPhysicalRoot + suffix;
        }

        // Rename each physical projection first. Preserve its actual parent/prefix: imported
        // Eudora bindings are allowed to differ from the visible canonical path.
        foreach (var binding in bindings.Where(candidate => !bindings.Any(ancestor =>
                     ancestor.Account == candidate.Account && ancestor.CanonicalPath.Length < candidate.CanonicalPath.Length &&
                     candidate.CanonicalPath.StartsWith(ancestor.CanonicalPath + "/", StringComparison.OrdinalIgnoreCase))))
            await RenameFolderPathAsync(binding.Account, binding.OldPath, PhysicalNewPath(binding));

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using (var updateNodes = connection.CreateCommand())
        {
            updateNodes.Transaction = transaction;
            updateNodes.CommandText = """
                UPDATE LocalFolderNode_shadow SET
                    canonical_path=$new || substr(canonical_path,length($old)+1),
                    name=CASE WHEN folder_id=$id THEN $name ELSE name END
                WHERE root_id=$root AND (canonical_path=$old OR canonical_path LIKE $prefix ESCAPE '\');
                """;
            updateNodes.Parameters.AddWithValue("$new", newPath);
            updateNodes.Parameters.AddWithValue("$old", oldPath);
            updateNodes.Parameters.AddWithValue("$id", folderId.ToString("D"));
            updateNodes.Parameters.AddWithValue("$name", newName);
            updateNodes.Parameters.AddWithValue("$root", rootId.ToString("D"));
            updateNodes.Parameters.AddWithValue("$prefix", oldPath.Replace("%", "\\%").Replace("_", "\\_") + "/%");
            await updateNodes.ExecuteNonQueryAsync();
        }
        await using (var updateBinding = connection.CreateCommand())
        {
            updateBinding.Transaction = transaction;
            updateBinding.CommandText = """
                UPDATE LocalFolderBinding_shadow SET legacy_full_name=$new
                 WHERE account_id=$account AND legacy_full_name=$old;
                """;
            var bindingNew = updateBinding.Parameters.Add("$new", SqliteType.Text);
            var bindingAccount = updateBinding.Parameters.Add("$account", SqliteType.Text);
            var bindingOld = updateBinding.Parameters.Add("$old", SqliteType.Text);
            foreach (var binding in bindings)
            {
                bindingNew.Value = PhysicalNewPath(binding);
                bindingAccount.Value = binding.Account.ToString("D");
                bindingOld.Value = binding.OldPath;
                await updateBinding.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
        return new CanonicalFolderMoveResult(oldPath, newPath, false);
    }

    public async Task DeleteCanonicalFolderTreeAsync(Guid folderId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        string root;
        string path;
        string? parent;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT root_id,canonical_path,parent_folder_id FROM LocalFolderNode_shadow WHERE folder_id=$id;";
            lookup.Parameters.AddWithValue("$id", folderId.ToString("D"));
            await using var reader = await lookup.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return;
            root = reader.GetString(0);
            path = reader.GetString(1);
            parent = reader.IsDBNull(2) ? null : reader.GetString(2);
        }
        if (parent == null) throw new InvalidOperationException("The tree root cannot be deleted.");

        var nodeIds = new List<string>();
        var bindings = new List<(string Account, string Path)>();
        var prefix = path.Replace("%", "\\%").Replace("_", "\\_") + "/%";
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT n.folder_id,b.account_id,b.legacy_full_name
                  FROM LocalFolderNode_shadow n
                  LEFT JOIN LocalFolderBinding_shadow b ON b.folder_id=n.folder_id
                 WHERE n.root_id=$root AND (n.canonical_path=$path OR n.canonical_path LIKE $prefix ESCAPE '\')
                 ORDER BY length(n.canonical_path) DESC;
                """;
            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$prefix", prefix);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetString(0);
                if (!nodeIds.Contains(id, StringComparer.OrdinalIgnoreCase)) nodeIds.Add(id);
                if (!reader.IsDBNull(1)) bindings.Add((reader.GetString(1), reader.GetString(2)));
            }
        }

        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();
        foreach (var id in nodeIds)
        {
            await using var deleteBinding = connection.CreateCommand();
            deleteBinding.Transaction = tx;
            deleteBinding.CommandText = "DELETE FROM LocalFolderBinding_shadow WHERE folder_id=$id;";
            deleteBinding.Parameters.AddWithValue("$id", id);
            await deleteBinding.ExecuteNonQueryAsync();

            await using var deleteNode = connection.CreateCommand();
            deleteNode.Transaction = tx;
            deleteNode.CommandText = "DELETE FROM LocalFolderNode_shadow WHERE folder_id=$id;";
            deleteNode.Parameters.AddWithValue("$id", id);
            await deleteNode.ExecuteNonQueryAsync();
        }
        foreach (var binding in bindings)
        {
            await using var deleteFolder = connection.CreateCommand();
            deleteFolder.Transaction = tx;
            deleteFolder.CommandText = """
                DELETE FROM Folder WHERE account_id=$account AND full_name=$path
                  AND NOT EXISTS(SELECT 1 FROM LocalFolderBinding_shadow
                                  WHERE account_id=$account AND legacy_full_name=$path);
                """;
            deleteFolder.Parameters.AddWithValue("$account", binding.Account);
            deleteFolder.Parameters.AddWithValue("$path", binding.Path);
            await deleteFolder.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public void Initialize()
    {
        var initializeStarted = Stopwatch.GetTimestamp();
        var checkpoint = initializeStarted;
        void RecordStage(string stage, string? details = null)
        {
            var now = Stopwatch.GetTimestamp();
            PerformanceLogService.Record($"SQLite initialization: {stage}",
                Stopwatch.GetElapsedTime(checkpoint, now), details);
            checkpoint = now;
        }

        var initialSize = File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0L;
        var initialVersion = 0;
        // Pre-migration backup: if we're upgrading from a pre-v2 (INTEGER unique_id) database,
        // copy mail.db to mail.db.pre-v2 before touching it. The v2 migration is one-way, so the
        // backup is the safety net. Preserved indefinitely; the user can delete it manually.
        if (File.Exists(_dbPath))
        {
            using var probe = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly;");
            probe.Open();
            initialVersion = GetUserVersion(probe);
            if (initialVersion < 2)
            {
                var backupPath = _dbPath + ".pre-v2";
                File.Copy(_dbPath, backupPath, overwrite: true);
                LogService.Log($"LocalStoreService: backed up mail.db to {backupPath} before v2 migration");
            }
        }
        RecordStage("read schema version and check backup",
            $"sizeBytes={initialSize}; userVersion={initialVersion}");

        using var conn = Open();
        RecordStage("open read/write connection");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS MessageSummary (
                unique_id    TEXT    NOT NULL,
                account_id   TEXT    NOT NULL,
                folder_name  TEXT    NOT NULL,
                from_disp    TEXT    NOT NULL DEFAULT '',
                to_addr      TEXT    NOT NULL DEFAULT '',
                subject      TEXT    NOT NULL DEFAULT '',
                date_ticks   INTEGER NOT NULL,
                is_read      INTEGER NOT NULL DEFAULT 0,
                preview_text TEXT    NOT NULL DEFAULT '',
                is_replied   INTEGER NOT NULL DEFAULT 0,
                is_forwarded INTEGER NOT NULL DEFAULT 0,
                message_direction INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (unique_id, account_id, folder_name)
            );
            CREATE INDEX IF NOT EXISTS idx_summary_date
                ON MessageSummary(date_ticks DESC);
            CREATE INDEX IF NOT EXISTS idx_summary_account_folder_date
                ON MessageSummary(account_id, folder_name, date_ticks DESC);

            CREATE TABLE IF NOT EXISTS MessageDetail (
                unique_id   TEXT    NOT NULL,
                account_id  TEXT    NOT NULL,
                folder_name TEXT    NOT NULL,
                to_addr     TEXT    NOT NULL DEFAULT '',
                cc          TEXT    NOT NULL DEFAULT '',
                bcc         TEXT    NOT NULL DEFAULT '',
                reply_to    TEXT    NOT NULL DEFAULT '',
                plain_body  TEXT    NOT NULL DEFAULT '',
                html_body   TEXT    NOT NULL DEFAULT '',
                PRIMARY KEY (unique_id, account_id, folder_name)
            );
            """;
        cmd.ExecuteNonQuery();
        RecordStage("base message tables and indexes");

        // Migration: add columns added after initial release.
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN to_addr        TEXT    NOT NULL DEFAULT '';");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN preview_text  TEXT    NOT NULL DEFAULT '';");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN is_replied    INTEGER NOT NULL DEFAULT 0;");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN is_forwarded  INTEGER NOT NULL DEFAULT 0;");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN has_attachments INTEGER NOT NULL DEFAULT 0;");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN is_mailing_list INTEGER NOT NULL DEFAULT 0;");
        RunMigration(conn, "ALTER TABLE MessageDetail ADD COLUMN attachments_json TEXT DEFAULT NULL;");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN flag_id TEXT DEFAULT NULL;");
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN message_direction INTEGER NOT NULL DEFAULT 0;");
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_summary_flag_date
                ON MessageSummary(flag_id, date_ticks DESC) WHERE flag_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_summary_flag_scope_date
                ON MessageSummary(account_id, folder_name, date_ticks DESC) WHERE flag_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_summary_direction_date
                ON MessageSummary(message_direction, date_ticks DESC);
            """;
        cmd.ExecuteNonQuery();
        RunMigration(conn, "ALTER TABLE MessageDetail ADD COLUMN calendar_ics TEXT DEFAULT NULL;");
        RunMigration(conn, "ALTER TABLE MessageDetail ADD COLUMN raw_headers TEXT NOT NULL DEFAULT '';");
        RunMigration(conn, "ALTER TABLE MessageDetail ADD COLUMN bcc TEXT NOT NULL DEFAULT '';");
        RunMigration(conn, "ALTER TABLE MessageDetail ADD COLUMN draft_compose_mode INTEGER NOT NULL DEFAULT 0;");
        RunMigration(conn, "ALTER TABLE MessageDetail ADD COLUMN draft_spell_language TEXT NOT NULL DEFAULT '';");
        // Stable RFC 5322 Message-ID for collapsing duplicate copies across folders (issue #220).
        // Adds the column for DBs already past the v1→v2 rebuild; fresh/v1 DBs get it from the
        // rebuild's schema below. No index: deduplication runs in memory (MessageDeduplicator), so
        // nothing queries this column — an index would only add upsert write cost.
        RunMigration(conn, "ALTER TABLE MessageSummary ADD COLUMN internet_message_id TEXT NOT NULL DEFAULT '';");
        RecordStage("message additive migrations and flag indexes");

        // CalendarEvent table (schema v4). Additive — no existing table touched.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS CalendarEvent (
                uid              TEXT    NOT NULL,
                account_id       TEXT    NOT NULL,
                summary          TEXT    NOT NULL DEFAULT '',
                description      TEXT    NOT NULL DEFAULT '',
                location         TEXT    NOT NULL DEFAULT '',
                organizer        TEXT    NOT NULL DEFAULT '',
                organizer_name   TEXT    NOT NULL DEFAULT '',
                start_time_ticks INTEGER DEFAULT NULL,
                end_time_ticks   INTEGER DEFAULT NULL,
                sequence         TEXT    DEFAULT NULL,
                method           TEXT    DEFAULT NULL,
                source_message_id TEXT   NOT NULL DEFAULT '',
                source_folder    TEXT    NOT NULL DEFAULT '',
                response_status  INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (uid, account_id)
            );
            CREATE INDEX IF NOT EXISTS idx_calendar_start
                ON CalendarEvent(start_time_ticks);
            """;
        cmd.ExecuteNonQuery();

        // All-day flag for locally-authored appointments. Idempotent ALTER (RunMigration ignores
        // "duplicate column" on databases that already have it).
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN is_all_day INTEGER NOT NULL DEFAULT 0;");

        // RRULE string for repeating appointments (null for one-offs). Idempotent ALTER.
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN recurrence_rule TEXT DEFAULT NULL;");

        // Excluded occurrence starts for a recurring master ("delete just this one"). Idempotent ALTER.
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN exdates TEXT DEFAULT NULL;");

        // Marks rows pulled from a Microsoft (Graph) calendar by GraphCalendarSyncService (M4
        // read-down v1). Graph rows are replaced wholesale per sync and must be excluded from the
        // invite harvest and orphan cleanup; account deletion still cascades by account_id.
        // Idempotent ALTER, mirroring is_all_day.
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN is_graph INTEGER NOT NULL DEFAULT 0;");

        // Per-calendar tagging (multi-calendar-per-account): the specific server calendar a synced
        // row belongs to, so one account's calendars show as separate tree nodes. Empty for local /
        // invite-harvested rows. Unversioned idempotent ALTERs, like is_all_day/is_graph above.
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN calendar_id   TEXT NOT NULL DEFAULT '';");
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN calendar_name TEXT NOT NULL DEFAULT '';");

        // CalDAV resource href for a server-synced iCloud row — the real URL the event lives at on
        // the server (Apple names iPhone/web-created resources randomly, ≠ UID), so edit/delete can
        // target it instead of a reconstructed {collection}/{uid}.ics. Empty for Graph/Google/local/
        // invite rows. Unversioned idempotent ALTER, like calendar_id/calendar_name above.
        RunMigration(conn, "ALTER TABLE CalendarEvent ADD COLUMN resource_url TEXT NOT NULL DEFAULT '';");
        RecordStage("calendar table, index and additive migrations");

        // Folder table (#516). The folder list used to live only in MainViewModel._cachedFolders,
        // filled by GetFoldersAsync after connect — so at launch nothing knew which folders existed,
        // let alone which were Inboxes. That made a startup folder impossible to honour before the
        // network came up, and left the folder tree showing only the virtual aggregates. Persisting
        // the list makes both work offline. Additive, no existing table touched, so no user_version
        // bump is needed (same reasoning as CalendarEvent above).
        //
        // SuppressUnreadCount is deliberately NOT a column — it is derived from Kind on the model.
        // Header rows are synthesized by RebuildFolderListFromCache and are never stored.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Folder (
                account_id            TEXT    NOT NULL,
                full_name             TEXT    NOT NULL,
                display_name          TEXT    NOT NULL DEFAULT '',
                parent_id             TEXT    DEFAULT NULL,
                kind                  INTEGER NOT NULL DEFAULT 0,
                exclude_from_all_mail INTEGER NOT NULL DEFAULT 0,
                unread_count          INTEGER NOT NULL DEFAULT 0,
                message_count         INTEGER NOT NULL DEFAULT 0,
                sort_order            INTEGER NOT NULL DEFAULT 0,
                is_container          INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (account_id, full_name)
            );

            CREATE TABLE IF NOT EXISTS Pop3Receipt (
                account_id    TEXT NOT NULL,
                uidl          TEXT NOT NULL,
                unique_id     TEXT NOT NULL,
                received_utc  INTEGER NOT NULL,
                PRIMARY KEY (account_id, uidl)
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS LocalMessageFts USING fts5(
                account_id UNINDEXED, unique_id UNINDEXED, folder_name UNINDEXED,
                from_addr, to_addr, cc_addr, subject, body_text,
                tokenize='unicode61 remove_diacritics 2'
            );

            -- FTS5 does not index the identity columns above. Keep a small ordinary
            -- index that translates a message key to its FTS rowid, so deleting a
            -- selection never scans every indexed message body.
            CREATE TABLE IF NOT EXISTS LocalMessageFtsKey (
                account_id  TEXT    NOT NULL,
                unique_id   TEXT    NOT NULL,
                folder_name TEXT    NOT NULL,
                fts_rowid   INTEGER NOT NULL,
                PRIMARY KEY (account_id, folder_name, unique_id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_local_fts_key_rowid
                ON LocalMessageFtsKey(fts_rowid);

            CREATE TABLE IF NOT EXISTS AttachmentContent (
                account_id      TEXT NOT NULL,
                unique_id       TEXT NOT NULL,
                folder_name     TEXT NOT NULL,
                attachment_name TEXT NOT NULL,
                entry_path      TEXT NOT NULL DEFAULT '',
                source_path     TEXT NOT NULL DEFAULT '',
                fingerprint     TEXT NOT NULL DEFAULT '',
                status          TEXT NOT NULL DEFAULT 'pending',
                content_text    TEXT NOT NULL DEFAULT '',
                fts_rowid       INTEGER,
                PRIMARY KEY(account_id, unique_id, folder_name, attachment_name, entry_path)
            );
            CREATE INDEX IF NOT EXISTS idx_attachment_message
                ON AttachmentContent(account_id, folder_name, unique_id);
            CREATE INDEX IF NOT EXISTS idx_attachment_fingerprint
                ON AttachmentContent(source_path, fingerprint);
            CREATE TABLE IF NOT EXISTS AttachmentFileHashCache (
                source_path          TEXT PRIMARY KEY,
                file_length          INTEGER NOT NULL,
                last_write_utc_ticks INTEGER NOT NULL,
                sha256               TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_attachment_file_hash
                ON AttachmentFileHashCache(sha256);
            CREATE TABLE IF NOT EXISTS AttachmentExtractionCache (
                sha256          TEXT NOT NULL,
                extraction_key  TEXT NOT NULL,
                entry_path      TEXT NOT NULL DEFAULT '',
                status          TEXT NOT NULL,
                content_text    TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(sha256, extraction_key, entry_path)
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS AttachmentContentFts USING fts5(
                attachment_name, entry_path, content_text,
                tokenize='unicode61 remove_diacritics 2'
            );
            """;
        cmd.ExecuteNonQuery();
        RecordStage("folder, search and attachment schemas");

        // Repair messages written by older builds before their local Draft/Scheduled folder was
        // catalogued. The GROUP BY scans the complete message table (very expensive on a large
        // Eudora import), so only run it when the newly-created/legacy Folder table is empty.
        // Current write/import paths maintain Folder themselves.
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM Folder LIMIT 1);";
        var folderCatalogueAlreadyPopulated = Convert.ToInt64(cmd.ExecuteScalar() ?? 0) != 0;
        if (!folderCatalogueAlreadyPopulated)
        {
            cmd.CommandText = """
            INSERT OR IGNORE INTO Folder
                (account_id,full_name,display_name,parent_id,kind,exclude_from_all_mail,
                 unread_count,message_count,sort_order,is_container)
            SELECT account_id,folder_name,folder_name,NULL,
                   CASE lower(folder_name)
                     WHEN 'inbox' THEN 1 WHEN 'draft' THEN 2 WHEN 'drafts' THEN 2
                     WHEN 'sent' THEN 3 WHEN 'trash' THEN 4 WHEN 'junk' THEN 5
                     WHEN 'scheduled' THEN 10 ELSE 0 END,
                   CASE lower(folder_name)
                     WHEN 'draft' THEN 1 WHEN 'drafts' THEN 1 WHEN 'sent' THEN 1
                     WHEN 'trash' THEN 1 WHEN 'scheduled' THEN 1 ELSE 0 END,
                   SUM(CASE WHEN is_read=0 THEN 1 ELSE 0 END),COUNT(*),0,0
            FROM MessageSummary
            WHERE folder_name<>''
            GROUP BY account_id,folder_name;
            """;
            cmd.ExecuteNonQuery();
        }
        RecordStage("repair folder catalogue from message summaries",
            folderCatalogueAlreadyPopulated ? "skipped=already-populated" : "performed=true");

        // Existing databases need one backfill. Once populated, all write paths maintain
        // the map and subsequent startups only perform the indexed existence check.
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM LocalMessageFtsKey LIMIT 1);";
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0) == 0)
        {
            cmd.CommandText = """
                INSERT INTO LocalMessageFtsKey(account_id,unique_id,folder_name,fts_rowid)
                    SELECT account_id,unique_id,folder_name,rowid FROM LocalMessageFts;
                """;
            cmd.ExecuteNonQuery();
        }
        RecordStage("check/backfill local FTS key map");

        RunMigration(conn, "ALTER TABLE Folder ADD COLUMN is_container INTEGER NOT NULL DEFAULT 0;");
        RecordStage("final folder additive migration");

        RunDataMigrations(conn);
        RecordStage("versioned data migrations", $"finalUserVersion={GetUserVersion(conn)}");

        // Run after versioned migrations because the v1→v2 conversion rebuilds MessageSummary and
        // consequently drops every index created against the original table. Every column exposed
        // as a message-grid sort gets both a global and an exact-folder ordering index.
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_summary_flag_date
                ON MessageSummary(flag_id, date_ticks DESC) WHERE flag_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_summary_flag_scope_date
                ON MessageSummary(account_id, folder_name, date_ticks DESC) WHERE flag_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_summary_from_date
                ON MessageSummary(from_disp COLLATE NOCASE, date_ticks DESC);
            CREATE INDEX IF NOT EXISTS idx_summary_to_date
                ON MessageSummary(to_addr COLLATE NOCASE, date_ticks DESC);
            CREATE INDEX IF NOT EXISTS idx_summary_subject_date
                ON MessageSummary(subject COLLATE NOCASE, date_ticks DESC);
            CREATE INDEX IF NOT EXISTS idx_summary_read_date
                ON MessageSummary(is_read, date_ticks DESC);
            CREATE INDEX IF NOT EXISTS idx_summary_attachments_date
                ON MessageSummary(has_attachments, date_ticks DESC);
            CREATE INDEX IF NOT EXISTS idx_summary_direction_date
                ON MessageSummary(message_direction, date_ticks DESC);
            CREATE INDEX IF NOT EXISTS idx_summary_account_folder_from_date
                ON MessageSummary(account_id, folder_name, from_disp COLLATE NOCASE, date_ticks DESC);
            CREATE INDEX IF NOT EXISTS idx_summary_account_folder_to_date
                ON MessageSummary(account_id, folder_name, to_addr COLLATE NOCASE, date_ticks DESC);
            CREATE INDEX IF NOT EXISTS idx_summary_account_folder_subject_date
                ON MessageSummary(account_id, folder_name, subject COLLATE NOCASE, date_ticks DESC);
            CREATE INDEX IF NOT EXISTS idx_summary_account_folder_read_date
                ON MessageSummary(account_id, folder_name, is_read, date_ticks DESC);
            CREATE INDEX IF NOT EXISTS idx_summary_account_folder_attachments_date
                ON MessageSummary(account_id, folder_name, has_attachments, date_ticks DESC);
            CREATE INDEX IF NOT EXISTS idx_summary_account_folder_direction_date
                ON MessageSummary(account_id, folder_name, message_direction, date_ticks DESC);
            """;
        cmd.ExecuteNonQuery();
        RecordStage("message-grid ordering indexes");
        PerformanceLogService.Record("SQLite initialization: total",
            Stopwatch.GetElapsedTime(initializeStarted),
            $"sizeBytes={initialSize}");
    }

    // SQLite's PRAGMA user_version stores a single integer per database. We use it as a
    // gate so data migrations run exactly once instead of on every startup — the to_addr
    // backfill in particular used to scan the whole MessageSummary table every launch.
    //
    // Migration numbering:
    //   0 → 1   to_addr backfill from MessageDetail
    //   1 → 2   unique_id INTEGER → TEXT (string MessageId); add DeltaToken table
    //   2 → 3   is_mailing_list backfill from to_addr patterns
    //   (no 3 → 4 data migration needed — CalendarEvent table + calendar_ics column
    //    added via CREATE TABLE IF NOT EXISTS / RunMigration; defaults are correct.
    //    Harvesting from existing calendar_ics rows happens on demand via CalendarService.RefreshAsync.)
    //   4 → 5   clear MessageSummary so the next sync backfills internet_message_id (issue #220
    //           duplicate-collapse). The Message-ID can't be reconstructed from cached rows, and
    //           the cache repopulates automatically on the next launch's sync. MessageDetail (bodies)
    //           is left intact — same key, still valid.
    //   5 → 6   persist message direction. The one-time path backfill recognises Out/Sent (including
    //           historical Eudora _Out trees) before the user starts filing sent mail elsewhere.
    //           Future writes carry direction explicitly; this scan must never repeat at startup.
    //   6 → 7   add FTS rows for cached IMAP/Graph summaries. Those backends cache headers and
    //           preview text before a body is opened; older builds only indexed authoritative
    //           POP3/local messages, so textual searches silently omitted remote Inbox mail.
    // Add new migrations as: if (version < 8) { ...; }
    private const int CurrentSchemaVersion = 7;

    private static void RunDataMigrations(SqliteConnection conn)
    {
        var version = GetUserVersion(conn);
        if (version >= CurrentSchemaVersion) return;

        if (version < 1)
        {
            using var backfillCmd = conn.CreateCommand();
            backfillCmd.CommandText = """
                UPDATE MessageSummary
                SET to_addr = COALESCE((
                    SELECT d.to_addr
                    FROM MessageDetail d
                    WHERE d.unique_id = MessageSummary.unique_id
                      AND d.account_id = MessageSummary.account_id
                      AND d.folder_name = MessageSummary.folder_name
                ), to_addr)
                WHERE to_addr = '';
                """;
            backfillCmd.ExecuteNonQuery();
        }

        if (version < 2)
        {
            // Convert unique_id from INTEGER to TEXT for both tables, and add the DeltaToken
            // table used by the Graph backend. Rebuild-and-rename is the standard SQLite idiom
            // for a column type change. Wrapped in a transaction: on failure the rollback leaves
            // the v1 schema intact (the mail.db.pre-v2 backup is the second safety net).
            using var tx = conn.BeginTransaction();
            using var migrateCmd = conn.CreateCommand();
            migrateCmd.Transaction = tx;
            migrateCmd.CommandText = """
                CREATE TABLE MessageSummary_v2 (
                    unique_id    TEXT    NOT NULL,
                    account_id   TEXT    NOT NULL,
                    folder_name  TEXT    NOT NULL,
                    from_disp    TEXT    NOT NULL DEFAULT '',
                    to_addr      TEXT    NOT NULL DEFAULT '',
                    subject      TEXT    NOT NULL DEFAULT '',
                    date_ticks   INTEGER NOT NULL,
                    is_read      INTEGER NOT NULL DEFAULT 0,
                    preview_text TEXT    NOT NULL DEFAULT '',
                    is_replied   INTEGER NOT NULL DEFAULT 0,
                    is_forwarded INTEGER NOT NULL DEFAULT 0,
                    has_attachments INTEGER NOT NULL DEFAULT 0,
                    is_mailing_list INTEGER NOT NULL DEFAULT 0,
                    flag_id      TEXT    DEFAULT NULL,
                    internet_message_id TEXT NOT NULL DEFAULT '',
                    message_direction INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (unique_id, account_id, folder_name)
                );
                INSERT INTO MessageSummary_v2
                SELECT CAST(unique_id AS TEXT), account_id, folder_name, from_disp, to_addr,
                       subject, date_ticks, is_read, preview_text, is_replied, is_forwarded,
                       has_attachments, is_mailing_list, flag_id, internet_message_id,
                       message_direction
                FROM MessageSummary;
                DROP TABLE MessageSummary;
                ALTER TABLE MessageSummary_v2 RENAME TO MessageSummary;
                CREATE INDEX idx_summary_date ON MessageSummary(date_ticks DESC);
                CREATE INDEX idx_summary_account_folder_date ON MessageSummary(account_id, folder_name, date_ticks DESC);

                CREATE TABLE MessageDetail_v2 (
                    unique_id   TEXT NOT NULL,
                    account_id  TEXT NOT NULL,
                    folder_name TEXT NOT NULL,
                    to_addr     TEXT NOT NULL DEFAULT '',
                    cc          TEXT NOT NULL DEFAULT '',
                    bcc         TEXT NOT NULL DEFAULT '',
                    reply_to    TEXT NOT NULL DEFAULT '',
                    plain_body  TEXT NOT NULL DEFAULT '',
                    html_body   TEXT NOT NULL DEFAULT '',
                    attachments_json TEXT DEFAULT NULL,
                    calendar_ics TEXT DEFAULT NULL,
                    raw_headers TEXT NOT NULL DEFAULT '',
                    draft_compose_mode INTEGER NOT NULL DEFAULT 0,
                    draft_spell_language TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (unique_id, account_id, folder_name)
                );
                INSERT INTO MessageDetail_v2
                SELECT CAST(unique_id AS TEXT), account_id, folder_name, to_addr, cc, bcc,
                       reply_to, plain_body, html_body, attachments_json, calendar_ics, raw_headers,
                       draft_compose_mode, draft_spell_language
                FROM MessageDetail;
                DROP TABLE MessageDetail;
                ALTER TABLE MessageDetail_v2 RENAME TO MessageDetail;

                CREATE TABLE DeltaToken (
                    account_id   TEXT NOT NULL,
                    folder_id    TEXT NOT NULL,
                    delta_token  TEXT NOT NULL,
                    updated_utc  INTEGER NOT NULL,
                    PRIMARY KEY (account_id, folder_id)
                );
                """;
            migrateCmd.ExecuteNonQuery();
            tx.Commit();
        }

        if (version < 3)
        {
            // Best-effort backfill: flag rows whose to_addr contains a recognisable
            // mailing-list domain. The IMAP List-Id header detection handles newly
            // synced messages; this covers rows already in the DB.
            using var mlCmd = conn.CreateCommand();
            mlCmd.CommandText = """
                UPDATE MessageSummary
                SET is_mailing_list = 1
                WHERE is_mailing_list = 0
                  AND (
                        to_addr LIKE '%.groups.io%'
                     OR to_addr LIKE '%freelists.org%'
                     OR to_addr LIKE '%@listserv.%'
                     OR to_addr LIKE '%@mailman.%'
                     OR to_addr LIKE '%yahoogroups.com%'
                     OR to_addr LIKE '%googlegroups.com%'
                  );
                """;
            mlCmd.ExecuteNonQuery();
        }

        if (version < 5)
        {
            // Purge cached summaries so the next sync repopulates internet_message_id, which
            // aggregate views need to collapse Gmail's per-folder duplicate copies (issue #220).
            using var clearCmd = conn.CreateCommand();
            clearCmd.CommandText = "DELETE FROM MessageSummary;";
            clearCmd.ExecuteNonQuery();
        }

        if (version < 6)
        {
            using var directionCmd = conn.CreateCommand();
            directionCmd.CommandText = """
                UPDATE MessageSummary
                   SET message_direction = CASE
                       WHEN instr('/'||lower(replace(folder_name,'\','/'))||'/', '/out/') > 0
                         OR instr('/'||lower(replace(folder_name,'\','/'))||'/', '/_out/') > 0
                         OR instr('/'||lower(replace(folder_name,'\','/'))||'/', '/sent/') > 0
                         OR instr('/'||lower(replace(folder_name,'\','/'))||'/', '/sent items/') > 0
                         OR instr('/'||lower(replace(folder_name,'\','/'))||'/', '/draft/') > 0
                         OR instr('/'||lower(replace(folder_name,'\','/'))||'/', '/drafts/') > 0
                         OR instr('/'||lower(replace(folder_name,'\','/'))||'/', '/scheduled/') > 0
                       THEN 2 ELSE 1 END
                 WHERE message_direction=0;
                """;
            directionCmd.ExecuteNonQuery();
        }

        if (version < 7)
        {
            using var tx = conn.BeginTransaction();
            using var ftsCmd = conn.CreateCommand();
            ftsCmd.Transaction = tx;
            ftsCmd.CommandText = """
                CREATE TEMP TABLE qm_fts_backfill_start(rowid INTEGER NOT NULL);
                INSERT INTO qm_fts_backfill_start
                    SELECT COALESCE(MAX(rowid), 0) FROM LocalMessageFts;

                INSERT INTO LocalMessageFts
                    (account_id,unique_id,folder_name,from_addr,to_addr,cc_addr,subject,body_text)
                SELECT s.account_id,s.unique_id,s.folder_name,s.from_disp,s.to_addr,
                       COALESCE(d.cc,''),s.subject,
                       CASE WHEN trim(COALESCE(d.plain_body,'')) <> '' THEN d.plain_body
                            WHEN trim(COALESCE(d.html_body,'')) <> '' THEN d.html_body
                            ELSE s.preview_text END
                  FROM MessageSummary s
                  LEFT JOIN MessageDetail d
                    ON d.account_id=s.account_id AND d.unique_id=s.unique_id
                   AND d.folder_name=s.folder_name
                 WHERE NOT EXISTS (
                       SELECT 1 FROM LocalMessageFtsKey k
                        WHERE k.account_id=s.account_id AND k.unique_id=s.unique_id
                          AND k.folder_name=s.folder_name);

                INSERT OR IGNORE INTO LocalMessageFtsKey(account_id,unique_id,folder_name,fts_rowid)
                    SELECT account_id,unique_id,folder_name,rowid
                      FROM LocalMessageFts
                     WHERE rowid > (SELECT rowid FROM qm_fts_backfill_start);
                DROP TABLE qm_fts_backfill_start;
                """;
            ftsCmd.ExecuteNonQuery();
            tx.Commit();
        }

        SetUserVersion(conn, CurrentSchemaVersion);
    }

    private static int GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static void SetUserVersion(SqliteConnection conn, int version)
    {
        using var cmd = conn.CreateCommand();
        // PRAGMA does not accept bound parameters; format the integer directly (safe — int).
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private static void RunMigration(SqliteConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch { /* column already exists — safe to ignore */ }
    }

    public async Task UpsertSummariesAsync(IEnumerable<MailMessageSummary> summaries)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MessageSummary(unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, is_mailing_list, flag_id, internet_message_id, message_direction)
            VALUES($uid, $aid, $fn, $from, $to, $subj, $dt, $read, $preview, $replied, $forwarded, $ml, $flag_id, $imid, $direction)
            ON CONFLICT(unique_id, account_id, folder_name) DO UPDATE SET
                from_disp       = excluded.from_disp,
                to_addr         = excluded.to_addr,
                subject         = excluded.subject,
                date_ticks      = excluded.date_ticks,
                is_read         = excluded.is_read,
                is_replied      = CASE WHEN is_replied = 1 THEN 1 ELSE excluded.is_replied END,
                is_forwarded    = excluded.is_forwarded,
                is_mailing_list = excluded.is_mailing_list,
                internet_message_id = CASE WHEN excluded.internet_message_id = '' THEN internet_message_id ELSE excluded.internet_message_id END,
                message_direction = CASE WHEN excluded.message_direction=0 THEN message_direction ELSE excluded.message_direction END,
                preview_text    = CASE WHEN excluded.preview_text = '' THEN preview_text ELSE excluded.preview_text END,
                flag_id         = CASE
                    WHEN flag_id IS NULL AND excluded.flag_id IS NOT NULL THEN excluded.flag_id
                    ELSE flag_id
                    END;
            """;
            // Local flags are authoritative. A server \Flagged value can seed the built-in flag,
            // but an ordinary refresh with no server flag must never erase a named local flag.
        var pUid       = cmd.Parameters.Add("$uid",       SqliteType.Text);
        var pAid       = cmd.Parameters.Add("$aid",       SqliteType.Text);
        var pFn        = cmd.Parameters.Add("$fn",        SqliteType.Text);
        var pFrom      = cmd.Parameters.Add("$from",      SqliteType.Text);
        var pTo        = cmd.Parameters.Add("$to",        SqliteType.Text);
        var pSubj      = cmd.Parameters.Add("$subj",      SqliteType.Text);
        var pDt        = cmd.Parameters.Add("$dt",        SqliteType.Integer);
        var pRead      = cmd.Parameters.Add("$read",      SqliteType.Integer);
        var pPreview   = cmd.Parameters.Add("$preview",   SqliteType.Text);
        var pReplied   = cmd.Parameters.Add("$replied",   SqliteType.Integer);
        var pForwarded = cmd.Parameters.Add("$forwarded", SqliteType.Integer);
        var pMl        = cmd.Parameters.Add("$ml",        SqliteType.Integer);
        var pFlagId    = cmd.Parameters.Add("$flag_id",   SqliteType.Text);
        var pImid      = cmd.Parameters.Add("$imid",      SqliteType.Text);
        var pDirection = cmd.Parameters.Add("$direction", SqliteType.Integer);

        // IMAP/Graph synchronization initially has only headers and PREVIEW text. Keep those rows
        // searchable immediately instead of waiting for the user to open every message body. If a
        // full detail is already cached, retain its richer body while refreshing the header fields.
        await using var fts = conn.CreateCommand();
        fts.CommandText = """
            UPDATE LocalMessageFts
               SET from_addr=$from,to_addr=$to,subject=$subj,
                   body_text=CASE WHEN EXISTS(
                       SELECT 1 FROM MessageDetail d
                        WHERE d.account_id=$aid AND d.unique_id=$uid AND d.folder_name=$fn
                          AND (trim(d.plain_body)<>'' OR trim(d.html_body)<>''))
                       THEN body_text ELSE $preview END
             WHERE rowid IN (SELECT fts_rowid FROM LocalMessageFtsKey
                              WHERE account_id=$aid AND unique_id=$uid AND folder_name=$fn);
            INSERT INTO LocalMessageFts
                (account_id,unique_id,folder_name,from_addr,to_addr,cc_addr,subject,body_text)
                SELECT $aid,$uid,$fn,$from,$to,'',$subj,$preview
                 WHERE NOT EXISTS (SELECT 1 FROM LocalMessageFtsKey
                                    WHERE account_id=$aid AND unique_id=$uid AND folder_name=$fn);
            INSERT INTO LocalMessageFtsKey(account_id,unique_id,folder_name,fts_rowid)
                SELECT $aid,$uid,$fn,last_insert_rowid()
                 WHERE changes()=1 AND NOT EXISTS (SELECT 1 FROM LocalMessageFtsKey
                                                   WHERE account_id=$aid AND unique_id=$uid AND folder_name=$fn);
            """;
        var fUid     = fts.Parameters.Add("$uid",     SqliteType.Text);
        var fAid     = fts.Parameters.Add("$aid",     SqliteType.Text);
        var fFn      = fts.Parameters.Add("$fn",      SqliteType.Text);
        var fFrom    = fts.Parameters.Add("$from",    SqliteType.Text);
        var fTo      = fts.Parameters.Add("$to",      SqliteType.Text);
        var fSubject = fts.Parameters.Add("$subj",    SqliteType.Text);
        var fPreview = fts.Parameters.Add("$preview", SqliteType.Text);

        foreach (var s in summaries)
        {
            pUid.Value       = s.MessageId;
            pAid.Value       = s.AccountId.ToString();
            pFn.Value        = s.FolderName;
            pFrom.Value      = s.From;
            pTo.Value        = s.To;
            pSubj.Value      = s.Subject;
            pDt.Value        = s.Date.UtcTicks;
            pRead.Value      = s.IsRead          ? 1 : 0;
            pPreview.Value   = s.Preview;
            pReplied.Value   = s.IsReplied        ? 1 : 0;
            pForwarded.Value = s.IsForwarded      ? 1 : 0;
            pMl.Value        = s.IsMailingList    ? 1 : 0;
            pFlagId.Value    = s.IsServerFlagged
                ? (object)FlagDefinition.BuiltInFlagId.ToString()
                : DBNull.Value;
            pImid.Value      = s.InternetMessageId ?? string.Empty;
            pDirection.Value = (int)s.Direction;
            await cmd.ExecuteNonQueryAsync();

            fUid.Value     = s.MessageId;
            fAid.Value     = s.AccountId.ToString();
            fFn.Value      = s.FolderName;
            fFrom.Value    = s.From ?? string.Empty;
            fTo.Value      = s.To ?? string.Empty;
            fSubject.Value = s.Subject ?? string.Empty;
            fPreview.Value = s.Preview ?? string.Empty;
            await fts.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<List<MailMessageSummary>> LoadAllSummariesAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id, internet_message_id, message_direction " +
            "FROM MessageSummary ORDER BY date_ticks DESC;";
        return await ReadSummariesAsync(cmd);
    }

    public async Task<List<MailMessageSummary>> LoadAllSummariesAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id, internet_message_id, message_direction " +
            "FROM MessageSummary WHERE account_id=$aid ORDER BY date_ticks DESC;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        return await ReadSummariesAsync(cmd);
    }

    public async Task<List<MailMessageSummary>> LoadFolderSummariesAsync(Guid accountId, string folderName, int? limit = null)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, account_id, folder_name, from_disp, to_addr, subject, date_ticks, is_read, preview_text, is_replied, is_forwarded, has_attachments, is_mailing_list, flag_id, internet_message_id, message_direction " +
            "FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn ORDER BY date_ticks DESC" +
            (limit.HasValue ? " LIMIT $limit;" : ";");
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        if (limit.HasValue)
            cmd.Parameters.AddWithValue("$limit", Math.Max(0, limit.Value));
        return await ReadSummariesAsync(cmd);
    }

    public async Task<bool> HasSummariesMissingRecipientsAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM MessageSummary WHERE to_addr = '' LIMIT 1);";
        var result = await cmd.ExecuteScalarAsync();
        return result is long value && value != 0;
    }

    public async Task DeleteSummariesAsync(Guid accountId, string folderName, IEnumerable<string> messageIds)
    {
        // Chunk so the IN list stays well under SQLite's compiled-parameter limit (~999).
        // Two round-trips per chunk regardless of size beats 2N round-trips for the old loop.
        const int chunkSize = 500;
        var ids = messageIds as IList<string> ?? messageIds.ToList();
        if (ids.Count == 0) return;

        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        for (int offset = 0; offset < ids.Count; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, ids.Count - offset);
            // Build "$u0,$u1,..." once for this chunk.
            var placeholders = string.Join(',', Enumerable.Range(0, count).Select(i => $"$u{i}"));
            await using var cmd = conn.CreateCommand();
            // Also clear source links in CalendarEvent for any deleted message that was
            // an invite source.  Cleared rather than deleted because the event itself
            // (the user's acceptance, the meeting time) should stay visible in the
            // calendar even after the original invite email is purged from the cache.
            cmd.CommandText =
                $"DELETE FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn AND unique_id IN ({placeholders});" +
                $"DELETE FROM MessageDetail  WHERE account_id=$aid AND folder_name=$fn AND unique_id IN ({placeholders});" +
                $"UPDATE CalendarEvent SET source_message_id='', source_folder='' WHERE account_id=$aid AND source_folder=$fn AND source_message_id IN ({placeholders});";
            cmd.Parameters.AddWithValue("$aid", accountId.ToString());
            cmd.Parameters.AddWithValue("$fn",  folderName);
            for (int i = 0; i < count; i++)
                cmd.Parameters.AddWithValue($"$u{i}", ids[offset + i]);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public async Task DeleteAccountDataAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var tx   = await conn.BeginTransactionAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM MessageDetail  WHERE account_id = $aid;" +
            "DELETE FROM MessageSummary WHERE account_id = $aid;" +
            "DELETE FROM CalendarEvent  WHERE account_id = $aid;" +
            "DELETE FROM Folder         WHERE account_id = $aid;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    /// <summary>
    /// Clears cached mail (summaries, bodies, delta cursors) for the given accounts only — used for
    /// the one-time Graph immutable-id rebuild (#366). Scoped to Graph accounts by the caller so IMAP
    /// bodies (and the IMAP calendar-invite source links that depend on them) are left intact.
    /// Calendar events are NOT touched. No-op for an empty set.
    /// </summary>
    public async Task ClearCachedMailAsync(IEnumerable<Guid> accountIds)
    {
        var ids = accountIds?.ToList() ?? [];
        if (ids.Count == 0) return;

        await using var conn = await OpenAsync();
        await using var tx   = await conn.BeginTransactionAsync();
        foreach (var id in ids)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM MessageDetail  WHERE account_id = $aid;" +
                "DELETE FROM MessageSummary WHERE account_id = $aid;" +
                "DELETE FROM DeltaToken     WHERE account_id = $aid;";
            cmd.Parameters.AddWithValue("$aid", id.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    // ── Folders (#516) ───────────────────────────────────────────────────────────

    public async Task SaveFoldersAsync(Guid accountId, IReadOnlyList<MailFolderModel> folders)
    {
        // Replace-all for this account, in one transaction: a folder deleted or renamed on the
        // server must disappear locally, and an upsert alone would leave the old row behind.
        // Other accounts are untouched, so a partial connect only refreshes what it reached.
        await using var conn = await OpenAsync();
        await using var tx   = await conn.BeginTransactionAsync();

        await using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM Folder WHERE account_id = $aid;";
            del.Parameters.AddWithValue("$aid", accountId.ToString());
            await del.ExecuteNonQueryAsync();
        }

        await using (var ins = conn.CreateCommand())
        {
            ins.CommandText = """
                INSERT OR REPLACE INTO Folder
                    (account_id, full_name, display_name, parent_id, kind,
                     exclude_from_all_mail, unread_count, message_count, sort_order, is_container)
                VALUES ($aid, $fn, $dn, $pid, $kind, $excl, $unread, $total, $ord, $container);
                """;
            var pFn    = ins.Parameters.Add("$fn",     Microsoft.Data.Sqlite.SqliteType.Text);
            var pDn    = ins.Parameters.Add("$dn",     Microsoft.Data.Sqlite.SqliteType.Text);
            var pPid   = ins.Parameters.Add("$pid",    Microsoft.Data.Sqlite.SqliteType.Text);
            var pKind  = ins.Parameters.Add("$kind",   Microsoft.Data.Sqlite.SqliteType.Integer);
            var pExcl  = ins.Parameters.Add("$excl",   Microsoft.Data.Sqlite.SqliteType.Integer);
            var pUnr   = ins.Parameters.Add("$unread", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pTot   = ins.Parameters.Add("$total",  Microsoft.Data.Sqlite.SqliteType.Integer);
            var pOrd   = ins.Parameters.Add("$ord",    Microsoft.Data.Sqlite.SqliteType.Integer);
            var pCont  = ins.Parameters.Add("$container", Microsoft.Data.Sqlite.SqliteType.Integer);
            ins.Parameters.AddWithValue("$aid", accountId.ToString());

            var order = 0;
            foreach (var f in folders)
            {
                // Header rows are synthesized for display and carry no server folder.
                if (f.IsHeader || string.IsNullOrEmpty(f.FullName)) continue;

                pFn.Value   = f.FullName;
                pDn.Value   = f.DisplayName ?? string.Empty;
                pPid.Value  = (object?)f.ParentId ?? DBNull.Value;
                pKind.Value = (int)f.Kind;
                pExcl.Value = f.ExcludeFromAllMail ? 1 : 0;
                pUnr.Value  = f.UnreadCount;
                pTot.Value  = f.MessageCount;
                pOrd.Value  = order++;
                pCont.Value = f.IsContainer ? 1 : 0;
                await ins.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
    }

    public async Task RenameFolderPathAsync(Guid accountId, string oldPath, string newPath)
    {
        using var timing = PerformanceLogService.Measure("Folder move: local database",
            $"account={accountId}; from={oldPath}; to={newPath}");
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
            throw new ArgumentException("Folder paths cannot be empty.");
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;

        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var exists = conn.CreateCommand())
        {
            exists.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            exists.CommandText = "SELECT 1 FROM Folder WHERE account_id=$aid AND full_name=$new LIMIT 1;";
            exists.Parameters.AddWithValue("$aid", accountId.ToString());
            exists.Parameters.AddWithValue("$new", newPath);
            if (await exists.ExecuteScalarAsync() != null)
                throw new InvalidOperationException($"A folder named '{newPath}' already exists.");
        }

        var paths = new List<(string OldPath, string NewPath, string NewParent)>();
        await using (var folders = conn.CreateCommand())
        {
            folders.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            folders.CommandText = """
                SELECT full_name, parent_id FROM Folder
                WHERE account_id=$aid
                  AND (full_name=$old OR substr(full_name,1,length($old)+1)=$prefix)
                ORDER BY length(full_name);
                """;
            folders.Parameters.AddWithValue("$aid", accountId.ToString());
            folders.Parameters.AddWithValue("$old", oldPath);
            folders.Parameters.AddWithValue("$prefix", oldPath + "/");
            await using var reader = await folders.ExecuteReaderAsync();
            var rootParent = newPath.Contains('/') ? newPath[..newPath.LastIndexOf('/')] : string.Empty;
            while (await reader.ReadAsync())
            {
                var current = reader.GetString(0);
                var parent = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var replacement = newPath + current[oldPath.Length..];
                var replacementParent = current == oldPath ? rootParent
                    : parent.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(parent, oldPath, StringComparison.OrdinalIgnoreCase)
                        ? newPath + parent[oldPath.Length..]
                        : parent;
                paths.Add((current, replacement, replacementParent));
            }
        }
        if (paths.Count == 0) throw new InvalidOperationException($"Folder '{oldPath}' was not found.");

        async Task ExecutePathUpdate(string sql, (string OldPath, string NewPath, string NewParent) path)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$aid", accountId.ToString());
            cmd.Parameters.AddWithValue("$old", path.OldPath);
            cmd.Parameters.AddWithValue("$new", path.NewPath);
            cmd.Parameters.AddWithValue("$parent", path.NewParent);
            await cmd.ExecuteNonQueryAsync();
        }

        // Every predicate below is an equality lookup backed by an existing index. In particular,
        // update FTS rows through LocalMessageFtsKey.rowid; filtering the FTS virtual table by its
        // UNINDEXED account/folder columns scanned the complete message index on every move.
        foreach (var path in paths)
        {
            await ExecutePathUpdate("""
                UPDATE MessageDetail SET folder_name=$new WHERE rowid IN (
                    SELECT d.rowid FROM MessageSummary s JOIN MessageDetail d
                      ON d.unique_id=s.unique_id AND d.account_id=s.account_id AND d.folder_name=s.folder_name
                    WHERE s.account_id=$aid AND s.folder_name=$old);
                """, path);
            await ExecutePathUpdate("""
                UPDATE LocalMessageFts SET folder_name=$new WHERE rowid IN (
                    SELECT fts_rowid FROM LocalMessageFtsKey
                    WHERE account_id=$aid AND folder_name=$old);
                """, path);
            await ExecutePathUpdate(
                "UPDATE LocalMessageFtsKey SET folder_name=$new WHERE account_id=$aid AND folder_name=$old;", path);
            await ExecutePathUpdate(
                "UPDATE AttachmentContent SET folder_name=$new WHERE account_id=$aid AND folder_name=$old;", path);
            await ExecutePathUpdate(
                "UPDATE CalendarEvent SET source_folder=$new WHERE account_id=$aid AND source_folder=$old;", path);
            await ExecutePathUpdate(
                "UPDATE MessageSummary SET folder_name=$new WHERE account_id=$aid AND folder_name=$old;", path);
            await ExecutePathUpdate(
                "UPDATE Folder SET full_name=$new,parent_id=$parent WHERE account_id=$aid AND full_name=$old;", path);
        }
        await using (var displayName = conn.CreateCommand())
        {
            displayName.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            displayName.CommandText = "UPDATE Folder SET display_name=$display WHERE account_id=$aid AND full_name=$path;";
            displayName.Parameters.AddWithValue("$display", newPath.Split('/')[^1]);
            displayName.Parameters.AddWithValue("$aid", accountId.ToString());
            displayName.Parameters.AddWithValue("$path", newPath);
            await displayName.ExecuteNonQueryAsync();
        }
        await using (var counts = conn.CreateCommand())
        {
            counts.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            counts.CommandText = """
                UPDATE Folder SET
                    message_count=(SELECT COUNT(*) FROM MessageSummary s
                                   WHERE s.account_id=Folder.account_id AND s.folder_name=Folder.full_name),
                    unread_count=(SELECT COUNT(*) FROM MessageSummary s
                                  WHERE s.account_id=Folder.account_id AND s.folder_name=Folder.full_name AND s.is_read=0)
                WHERE account_id=$aid AND (full_name=$path OR substr(full_name,1,length($path)+1)=$prefix);
                """;
            counts.Parameters.AddWithValue("$aid", accountId.ToString());
            counts.Parameters.AddWithValue("$path", newPath);
            counts.Parameters.AddWithValue("$prefix", newPath + "/");
            await counts.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    /// <summary>
    /// Physically merges the obsolete local POP3 folder name <c>Inbox</c> into Eudora's canonical
    /// <c>In</c>. The caller supplies local-account ids deliberately: an IMAP account's INBOX is a
    /// server-owned name and must never be rewritten. Idempotent and safe to run at every startup;
    /// after the one-time conversion each account costs only indexed existence probes.
    /// </summary>
    public int NormalizeLocalInboxFolders(IEnumerable<Guid> localAccountIds)
    {
        var accounts = localAccountIds.Distinct().ToList();
        if (accounts.Count == 0) return 0;

        const string oldPath = "Inbox";
        const string newPath = "In";
        using var timing = PerformanceLogService.Measure("SQLite migration: normalize local Inbox to In",
            $"accounts={accounts.Count}");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var hasCanonicalBindings = TableExists(connection, transaction, "LocalFolderBinding_shadow");
        var migratedMessages = 0;

        foreach (var accountId in accounts)
        {
            using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM Folder WHERE account_id=$aid AND full_name=$old
                    UNION ALL SELECT 1 FROM MessageSummary WHERE account_id=$aid AND folder_name=$old
                    LIMIT 1);
                """;
            exists.Parameters.AddWithValue("$aid", accountId.ToString("D"));
            exists.Parameters.AddWithValue("$old", oldPath);
            if (Convert.ToInt32(exists.ExecuteScalar() ?? 0) == 0)
            {
                // A previous build may already have moved all mail but left only the old shadow
                // binding. Repair that cheap metadata residue below if the table exists.
                if (hasCanonicalBindings)
                    NormalizeInboxBinding(connection, transaction, accountId, oldPath, newPath);
                continue;
            }

            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM MessageSummary WHERE account_id=$aid AND folder_name=$old;";
                count.Parameters.AddWithValue("$aid", accountId.ToString("D"));
                count.Parameters.AddWithValue("$old", oldPath);
                migratedMessages += Convert.ToInt32(count.ExecuteScalar() ?? 0);
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$aid", accountId.ToString("D"));
            command.Parameters.AddWithValue("$old", oldPath);
            command.Parameters.AddWithValue("$new", newPath);
            command.Parameters.AddWithValue("$inboxKind", (int)SpecialFolderKind.Inbox);
            command.CommandText = """
                -- Preserve an existing In row when both aliases are present, otherwise clone the
                -- Inbox catalogue entry before moving keyed message rows.
                INSERT OR IGNORE INTO Folder(account_id,full_name,display_name,parent_id,kind,
                    exclude_from_all_mail,unread_count,message_count,sort_order,is_container)
                SELECT account_id,$new,$new,parent_id,$inboxKind,exclude_from_all_mail,
                       unread_count,message_count,sort_order,is_container
                  FROM Folder WHERE account_id=$aid AND full_name=$old;

                -- Remove only source index rows whose message already exists at the destination.
                -- Each ordinary key table is handled before its parent virtual-FTS row is changed.
                DELETE FROM LocalMessageFts WHERE rowid IN (
                    SELECT source.fts_rowid FROM LocalMessageFtsKey source
                     WHERE source.account_id=$aid AND source.folder_name=$old AND EXISTS(
                           SELECT 1 FROM LocalMessageFtsKey target
                            WHERE target.account_id=$aid AND target.folder_name=$new
                              AND target.unique_id=source.unique_id));
                DELETE FROM LocalMessageFtsKey
                 WHERE account_id=$aid AND folder_name=$old AND EXISTS(
                       SELECT 1 FROM LocalMessageFtsKey target
                        WHERE target.account_id=$aid AND target.folder_name=$new
                          AND target.unique_id=LocalMessageFtsKey.unique_id);

                DELETE FROM AttachmentContentFts WHERE rowid IN (
                    SELECT source.fts_rowid FROM AttachmentContent source
                     WHERE source.account_id=$aid AND source.folder_name=$old
                       AND source.fts_rowid IS NOT NULL AND EXISTS(
                           SELECT 1 FROM AttachmentContent target
                            WHERE target.account_id=$aid AND target.folder_name=$new
                              AND target.unique_id=source.unique_id
                              AND target.attachment_name=source.attachment_name
                              AND target.entry_path=source.entry_path));
                DELETE FROM AttachmentContent
                 WHERE account_id=$aid AND folder_name=$old AND EXISTS(
                       SELECT 1 FROM AttachmentContent target
                        WHERE target.account_id=$aid AND target.folder_name=$new
                          AND target.unique_id=AttachmentContent.unique_id
                          AND target.attachment_name=AttachmentContent.attachment_name
                          AND target.entry_path=AttachmentContent.entry_path);

                DELETE FROM MessageDetail
                 WHERE account_id=$aid AND folder_name=$old AND EXISTS(
                       SELECT 1 FROM MessageDetail target
                        WHERE target.account_id=$aid AND target.folder_name=$new
                          AND target.unique_id=MessageDetail.unique_id);
                DELETE FROM MessageSummary
                 WHERE account_id=$aid AND folder_name=$old AND EXISTS(
                       SELECT 1 FROM MessageSummary target
                        WHERE target.account_id=$aid AND target.folder_name=$new
                          AND target.unique_id=MessageSummary.unique_id);

                UPDATE MessageDetail SET folder_name=$new
                 WHERE account_id=$aid AND folder_name=$old;
                UPDATE LocalMessageFts SET folder_name=$new WHERE rowid IN (
                    SELECT fts_rowid FROM LocalMessageFtsKey
                     WHERE account_id=$aid AND folder_name=$old);
                UPDATE LocalMessageFtsKey SET folder_name=$new
                 WHERE account_id=$aid AND folder_name=$old;
                UPDATE AttachmentContent SET folder_name=$new
                 WHERE account_id=$aid AND folder_name=$old;
                UPDATE CalendarEvent SET source_folder=$new
                 WHERE account_id=$aid AND source_folder=$old;
                UPDATE MessageSummary SET folder_name=$new
                 WHERE account_id=$aid AND folder_name=$old;

                DELETE FROM Folder WHERE account_id=$aid AND full_name=$old;
                UPDATE Folder SET display_name=$new,kind=$inboxKind,
                    message_count=(SELECT COUNT(*) FROM MessageSummary
                                    WHERE account_id=$aid AND folder_name=$new),
                    unread_count=(SELECT COUNT(*) FROM MessageSummary
                                   WHERE account_id=$aid AND folder_name=$new AND is_read=0)
                 WHERE account_id=$aid AND full_name=$new;
                """;
            command.ExecuteNonQuery();

            if (hasCanonicalBindings)
                NormalizeInboxBinding(connection, transaction, accountId, oldPath, newPath);
        }

        transaction.Commit();
        if (migratedMessages > 0)
            LogService.Log($"Local Inbox normalization: migrated {migratedMessages:N0} message rows from Inbox to In.");
        return migratedMessages;
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0) != 0;
    }

    private static void NormalizeInboxBinding(SqliteConnection connection, SqliteTransaction transaction,
        Guid accountId, string oldPath, string newPath)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO LocalFolderBinding_shadow(account_id,legacy_full_name,folder_id)
            SELECT account_id,$new,folder_id FROM LocalFolderBinding_shadow
             WHERE account_id=$aid AND legacy_full_name=$old;
            DELETE FROM LocalFolderBinding_shadow
             WHERE account_id=$aid AND legacy_full_name=$old;
            """;
        command.Parameters.AddWithValue("$aid", accountId.ToString("D"));
        command.Parameters.AddWithValue("$old", oldPath);
        command.Parameters.AddWithValue("$new", newPath);
        command.ExecuteNonQuery();
    }

    public async Task MergeFolderPathAsync(Guid accountId, string oldPath, string existingPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(existingPath))
            throw new ArgumentException("Folder paths cannot be empty.");
        if (string.Equals(oldPath, existingPath, StringComparison.OrdinalIgnoreCase)) return;

        await using var conn = await OpenAsync();
        await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var paths = new List<(string Old, string New)>();
        await using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = """
                SELECT full_name FROM Folder
                WHERE account_id=$aid AND (full_name=$old OR (full_name >= $prefix AND full_name < $end))
                ORDER BY length(full_name) DESC;
                """;
            find.Parameters.AddWithValue("$aid", accountId.ToString());
            find.Parameters.AddWithValue("$old", oldPath);
            find.Parameters.AddWithValue("$prefix", oldPath + "/");
            find.Parameters.AddWithValue("$end", oldPath + "0");
            await using var reader = await find.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var current = reader.GetString(0);
                paths.Add((current, existingPath + current[oldPath.Length..]));
            }
        }
        if (paths.Count == 0) throw new InvalidOperationException($"Folder '{oldPath}' was not found.");

        foreach (var path in paths)
        {
            await using var command = conn.CreateCommand();
            command.Transaction = tx;
            command.Parameters.AddWithValue("$aid", accountId.ToString());
            command.Parameters.AddWithValue("$old", path.Old);
            command.Parameters.AddWithValue("$new", path.New);
            command.Parameters.AddWithValue("$parent", path.New.Contains('/')
                ? path.New[..path.New.LastIndexOf('/')] : string.Empty);
            command.CommandText = """
                -- If the same physical message is already present in the destination, retain that
                -- copy and discard only the duplicate source rows before changing folder keys.
                DELETE FROM LocalMessageFts WHERE rowid IN (
                    SELECT k.fts_rowid FROM LocalMessageFtsKey k
                    WHERE k.account_id=$aid AND k.folder_name=$old AND EXISTS (
                        SELECT 1 FROM MessageSummary d WHERE d.account_id=$aid
                        AND d.folder_name=$new AND d.unique_id=k.unique_id));
                DELETE FROM LocalMessageFtsKey WHERE account_id=$aid AND folder_name=$old AND EXISTS (
                    SELECT 1 FROM MessageSummary d WHERE d.account_id=$aid
                    AND d.folder_name=$new AND d.unique_id=LocalMessageFtsKey.unique_id);
                DELETE FROM MessageDetail WHERE account_id=$aid AND folder_name=$old AND EXISTS (
                    SELECT 1 FROM MessageSummary d WHERE d.account_id=$aid
                    AND d.folder_name=$new AND d.unique_id=MessageDetail.unique_id);
                DELETE FROM AttachmentContent WHERE account_id=$aid AND folder_name=$old AND EXISTS (
                    SELECT 1 FROM AttachmentContent d WHERE d.account_id=$aid AND d.folder_name=$new
                    AND d.unique_id=AttachmentContent.unique_id
                    AND d.attachment_name=AttachmentContent.attachment_name
                    AND d.entry_path=AttachmentContent.entry_path);
                DELETE FROM MessageSummary WHERE account_id=$aid AND folder_name=$old AND EXISTS (
                    SELECT 1 FROM MessageSummary d WHERE d.account_id=$aid
                    AND d.folder_name=$new AND d.unique_id=MessageSummary.unique_id);

                UPDATE MessageDetail SET folder_name=$new WHERE account_id=$aid AND folder_name=$old;
                UPDATE LocalMessageFts SET folder_name=$new WHERE rowid IN (
                    SELECT fts_rowid FROM LocalMessageFtsKey WHERE account_id=$aid AND folder_name=$old);
                UPDATE LocalMessageFtsKey SET folder_name=$new WHERE account_id=$aid AND folder_name=$old;
                UPDATE AttachmentContent SET folder_name=$new WHERE account_id=$aid AND folder_name=$old;
                UPDATE CalendarEvent SET source_folder=$new WHERE account_id=$aid AND source_folder=$old;
                UPDATE MessageSummary SET folder_name=$new WHERE account_id=$aid AND folder_name=$old;

                DELETE FROM Folder WHERE account_id=$aid AND full_name=$old
                    AND EXISTS (SELECT 1 FROM Folder WHERE account_id=$aid AND full_name=$new);
                UPDATE Folder SET full_name=$new,parent_id=NULLIF($parent,'')
                    WHERE account_id=$aid AND full_name=$old;
                """;
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var counts = conn.CreateCommand())
        {
            counts.Transaction = tx;
            counts.Parameters.AddWithValue("$aid", accountId.ToString());
            counts.CommandText = """
                UPDATE Folder SET
                    message_count=(SELECT count(*) FROM MessageSummary s
                                   WHERE s.account_id=Folder.account_id AND s.folder_name=Folder.full_name),
                    unread_count=(SELECT count(*) FROM MessageSummary s
                                  WHERE s.account_id=Folder.account_id AND s.folder_name=Folder.full_name AND s.is_read=0)
                WHERE account_id=$aid;
                """;
            await counts.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<Dictionary<Guid, List<MailFolderModel>>> LoadFoldersAsync()
    {
        var result = new Dictionary<Guid, List<MailFolderModel>>();
        await using var conn = await OpenAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT account_id, full_name, display_name, parent_id, kind,
                   exclude_from_all_mail, unread_count, message_count, is_container
            FROM Folder
            ORDER BY account_id, sort_order;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            // A row whose account_id no longer parses is unusable; skip rather than throw and
            // lose every other account's folders on one bad value.
            if (!Guid.TryParse(r.GetString(0), out var accountId)) continue;

            if (!result.TryGetValue(accountId, out var list))
                result[accountId] = list = [];

            list.Add(new MailFolderModel
            {
                AccountId          = accountId,
                FullName           = r.GetString(1),
                DisplayName        = r.GetString(2),
                ParentId           = r.IsDBNull(3) ? null : r.GetString(3),
                Kind               = (SpecialFolderKind)r.GetInt32(4),
                ExcludeFromAllMail = r.GetInt32(5) != 0,
                UnreadCount        = r.GetInt32(6),
                MessageCount       = r.GetInt32(7),
                IsContainer        = r.GetInt32(8) != 0,
            });
        }
        return result;
    }

    public async Task EnsureLocalSystemFoldersAsync(IReadOnlyCollection<AccountModel> accounts)
    {
        var localAccounts = accounts
            .Where(account => account.BackendKind is BackendKind.Pop3Smtp or BackendKind.LocalArchive)
            .ToList();
        if (localAccounts.Count == 0) return;

        await using var connection = await OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS LocalFolderNode_shadow (
                    folder_id TEXT PRIMARY KEY, root_id TEXT NOT NULL, parent_folder_id TEXT NULL,
                    name TEXT NOT NULL, canonical_path TEXT NOT NULL, kind INTEGER NOT NULL DEFAULT 0,
                    is_container INTEGER NOT NULL DEFAULT 0, UNIQUE(root_id,canonical_path));
                CREATE TABLE IF NOT EXISTS LocalFolderBinding_shadow (
                    account_id TEXT NOT NULL, legacy_full_name TEXT NOT NULL, folder_id TEXT NOT NULL,
                    PRIMARY KEY(account_id,legacy_full_name),
                    FOREIGN KEY(folder_id) REFERENCES LocalFolderNode_shadow(folder_id));
                CREATE INDEX IF NOT EXISTS idx_local_folder_shadow_parent
                    ON LocalFolderNode_shadow(root_id,parent_folder_id,name);
                CREATE INDEX IF NOT EXISTS idx_local_binding_shadow_folder
                    ON LocalFolderBinding_shadow(folder_id);
                """;
            await schema.ExecuteNonQueryAsync();
        }

        foreach (var rootGroup in localAccounts.GroupBy(account => account.FolderTreeRootId ?? account.Id))
        {
            var rootId = rootGroup.Key;
            var rootName = rootGroup.Select(account => account.FolderTreeRootName)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? rootGroup.First().AccountLabel;
            var rootNodeId = await FindOrCreateRootAsync(rootId, rootName);

            foreach (var account in rootGroup)
            {
                var definitions = new List<(SpecialFolderKind Kind, string Name)>
                {
                    (SpecialFolderKind.Drafts, "Draft"),
                    (SpecialFolderKind.Scheduled, "Scheduled"),
                    (SpecialFolderKind.Trash, "Trash"),
                    (SpecialFolderKind.Junk, "Junk"),
                };
                if (account.BackendKind == BackendKind.Pop3Smtp)
                {
                    definitions.Insert(0, (SpecialFolderKind.Inbox, "In"));
                    definitions.Insert(3, (SpecialFolderKind.Sent, "Sent"));
                }

                foreach (var definition in definitions)
                {
                    var physicalPath = await FindPhysicalPathAsync(account.Id, definition.Kind)
                        ?? definition.Name;
                    var excluded = definition.Kind is SpecialFolderKind.Drafts or SpecialFolderKind.Scheduled;

                    await using (var folder = connection.CreateCommand())
                    {
                        folder.Transaction = transaction;
                        folder.CommandText = """
                            INSERT INTO Folder(account_id,full_name,display_name,parent_id,kind,
                                exclude_from_all_mail,unread_count,message_count,sort_order,is_container)
                            VALUES($account,$path,$name,NULL,$kind,$exclude,
                                (SELECT count(*) FROM MessageSummary WHERE account_id=$account AND folder_name=$path AND is_read=0),
                                (SELECT count(*) FROM MessageSummary WHERE account_id=$account AND folder_name=$path),0,0)
                            ON CONFLICT(account_id,full_name) DO UPDATE SET
                                kind=excluded.kind,exclude_from_all_mail=excluded.exclude_from_all_mail;
                            """;
                        folder.Parameters.AddWithValue("$account", account.Id.ToString("D"));
                        folder.Parameters.AddWithValue("$path", physicalPath);
                        folder.Parameters.AddWithValue("$name", definition.Name);
                        folder.Parameters.AddWithValue("$kind", (int)definition.Kind);
                        folder.Parameters.AddWithValue("$exclude", excluded ? 1 : 0);
                        await folder.ExecuteNonQueryAsync();
                    }

                    var canonicalId = await FindOrCreateSystemNodeAsync(
                        rootId, rootNodeId, definition.Kind, definition.Name);
                    await using var binding = connection.CreateCommand();
                    binding.Transaction = transaction;
                    binding.CommandText = """
                        INSERT INTO LocalFolderBinding_shadow(account_id,legacy_full_name,folder_id)
                        VALUES($account,$path,$folder)
                        ON CONFLICT(account_id,legacy_full_name) DO UPDATE SET folder_id=excluded.folder_id;
                        """;
                    binding.Parameters.AddWithValue("$account", account.Id.ToString("D"));
                    binding.Parameters.AddWithValue("$path", physicalPath);
                    binding.Parameters.AddWithValue("$folder", canonicalId.ToString("D"));
                    await binding.ExecuteNonQueryAsync();
                }
            }
        }

        await transaction.CommitAsync();

        async Task<Guid> FindOrCreateRootAsync(Guid rootId, string name)
        {
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT folder_id FROM LocalFolderNode_shadow WHERE root_id=$root AND canonical_path='' LIMIT 1;";
            lookup.Parameters.AddWithValue("$root", rootId.ToString("D"));
            if (await lookup.ExecuteScalarAsync() is string existing) return Guid.Parse(existing);

            var id = Guid.NewGuid();
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO LocalFolderNode_shadow(folder_id,root_id,parent_folder_id,name,canonical_path,kind,is_container)
                VALUES($id,$root,NULL,$name,'',0,1);
                """;
            insert.Parameters.AddWithValue("$id", id.ToString("D"));
            insert.Parameters.AddWithValue("$root", rootId.ToString("D"));
            insert.Parameters.AddWithValue("$name", name);
            await insert.ExecuteNonQueryAsync();
            return id;
        }

        async Task<string?> FindPhysicalPathAsync(Guid accountId, SpecialFolderKind kind)
        {
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT full_name FROM Folder WHERE account_id=$account AND kind=$kind ORDER BY length(full_name),full_name LIMIT 1;";
            lookup.Parameters.AddWithValue("$account", accountId.ToString("D"));
            lookup.Parameters.AddWithValue("$kind", (int)kind);
            return await lookup.ExecuteScalarAsync() as string;
        }

        async Task<Guid> FindOrCreateSystemNodeAsync(Guid rootId, Guid parentId,
            SpecialFolderKind kind, string name)
        {
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = transaction;
            lookup.CommandText = """
                SELECT folder_id FROM LocalFolderNode_shadow
                 WHERE root_id=$root AND (kind=$kind OR canonical_path=$name COLLATE NOCASE)
                 ORDER BY CASE WHEN kind=$kind THEN 0 ELSE 1 END,length(canonical_path) LIMIT 1;
                """;
            lookup.Parameters.AddWithValue("$root", rootId.ToString("D"));
            lookup.Parameters.AddWithValue("$kind", (int)kind);
            lookup.Parameters.AddWithValue("$name", name);
            if (await lookup.ExecuteScalarAsync() is string existing)
            {
                await using var repair = connection.CreateCommand();
                repair.Transaction = transaction;
                repair.CommandText = "UPDATE LocalFolderNode_shadow SET kind=$kind WHERE folder_id=$id;";
                repair.Parameters.AddWithValue("$kind", (int)kind);
                repair.Parameters.AddWithValue("$id", existing);
                await repair.ExecuteNonQueryAsync();
                return Guid.Parse(existing);
            }

            var id = Guid.NewGuid();
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO LocalFolderNode_shadow(folder_id,root_id,parent_folder_id,name,canonical_path,kind,is_container)
                VALUES($id,$root,$parent,$name,$name,$kind,0);
                """;
            insert.Parameters.AddWithValue("$id", id.ToString("D"));
            insert.Parameters.AddWithValue("$root", rootId.ToString("D"));
            insert.Parameters.AddWithValue("$parent", parentId.ToString("D"));
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$kind", (int)kind);
            await insert.ExecuteNonQueryAsync();
            return id;
        }
    }

    public async Task PurgeFoldersForUnknownAccountsAsync(IReadOnlyCollection<Guid> knownAccountIds)
    {
        await using var conn = await OpenAsync();

        // Unlike calendar events there is no Guid.Empty "local" bucket to preserve — every folder
        // belongs to a real account, so an unrecognised id is always an orphan.
        var keep = new HashSet<string>(knownAccountIds.Select(id => id.ToString()),
                                       StringComparer.OrdinalIgnoreCase);

        var present = new List<string>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT DISTINCT account_id FROM Folder;";
            await using var r = await q.ExecuteReaderAsync();
            while (await r.ReadAsync()) present.Add(r.GetString(0));
        }

        var orphans = present.Where(a => !keep.Contains(a)).ToList();
        if (orphans.Count == 0) return;

        await using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM Folder WHERE account_id = $aid;";
        var p = del.CreateParameter();
        p.ParameterName = "$aid";
        del.Parameters.Add(p);
        foreach (var orphan in orphans)
        {
            p.Value = orphan;
            await del.ExecuteNonQueryAsync();
        }
        LogService.Log($"LocalStoreService: purged folders for {orphans.Count} unknown account(s).");
    }

    /// <summary>
    /// Deletes calendar events whose <c>account_id</c> is not among <paramref name="knownAccountIds"/> —
    /// orphans left behind when an account is removed and re-added (the re-added account gets a new id,
    /// so the old id's events linger and show as duplicates), or after a cache rebuild. Local events
    /// (<see cref="Guid.Empty"/>) are always kept. No-op when there are no orphans.
    /// </summary>
    public async Task PurgeCalendarEventsForUnknownAccountsAsync(IReadOnlyCollection<Guid> knownAccountIds)
    {
        await using var conn = await OpenAsync();

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Guid.Empty.ToString() };
        foreach (var id in knownAccountIds) keep.Add(id.ToString());

        var present = new List<string>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT DISTINCT account_id FROM CalendarEvent;";
            await using var r = await q.ExecuteReaderAsync();
            while (await r.ReadAsync()) present.Add(r.GetString(0));
        }

        var orphans = present.Where(a => !keep.Contains(a)).ToList();
        if (orphans.Count == 0) return;

        await using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM CalendarEvent WHERE account_id = $aid;";
        var p = del.CreateParameter();
        p.ParameterName = "$aid";
        del.Parameters.Add(p);
        foreach (var orphan in orphans)
        {
            p.Value = orphan;
            await del.ExecuteNonQueryAsync();
        }
        LogService.Log($"LocalStoreService: purged calendar events for {orphans.Count} unknown account(s).");
    }

    public Task UpdateIsReadAsync(Guid accountId, string folderName, string messageId, bool isRead) =>
        UpdateIsReadBatchAsync([(accountId, folderName, messageId)], isRead);

    public async Task UpdateSenderAsync(Guid accountId, string folderName, string messageId, string sender)
    {
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        await using (var summary = conn.CreateCommand())
        {
            summary.Transaction = tx;
            summary.CommandText = """
                UPDATE MessageSummary SET from_disp=$from
                 WHERE account_id=$aid AND folder_name=$fn AND unique_id=$uid;
                """;
            summary.Parameters.AddWithValue("$from", sender ?? string.Empty);
            summary.Parameters.AddWithValue("$aid", accountId.ToString());
            summary.Parameters.AddWithValue("$fn", folderName);
            summary.Parameters.AddWithValue("$uid", messageId);
            await summary.ExecuteNonQueryAsync();
        }
        await using (var fts = conn.CreateCommand())
        {
            fts.Transaction = tx;
            fts.CommandText = """
                UPDATE LocalMessageFts SET from_addr=$from
                 WHERE rowid IN (SELECT fts_rowid FROM LocalMessageFtsKey
                                  WHERE account_id=$aid AND folder_name=$fn AND unique_id=$uid);
                """;
            fts.Parameters.AddWithValue("$from", sender ?? string.Empty);
            fts.Parameters.AddWithValue("$aid", accountId.ToString());
            fts.Parameters.AddWithValue("$fn", folderName);
            fts.Parameters.AddWithValue("$uid", messageId);
            await fts.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task UpdateIsRepliedAsync(Guid accountId, string folderName, string messageId,
        string? internetMessageId = null)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        // The folder path can change while a reply is being composed. The exact identity handles
        // messages without an RFC id; that RFC id also finds a copy moved to another folder.
        cmd.CommandText = """
            UPDATE MessageSummary SET is_replied=1
            WHERE account_id=$aid AND
                  ((folder_name=$fn AND unique_id=$uid) OR
                   ($imid<>'' AND internet_message_id=$imid));
            """;
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn", folderName);
        cmd.Parameters.AddWithValue("$uid", messageId);
        cmd.Parameters.AddWithValue("$imid", internetMessageId ?? string.Empty);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateIsReadBatchAsync(IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items, bool isRead)
    {
        var materialized = items.ToList();
        if (materialized.Count == 0) return;
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
        cmd.CommandText =
            "UPDATE MessageSummary SET is_read=$read " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        var pRead = cmd.Parameters.Add("$read", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pUid  = cmd.Parameters.Add("$uid",  Microsoft.Data.Sqlite.SqliteType.Text);
        var pAid  = cmd.Parameters.Add("$aid",  Microsoft.Data.Sqlite.SqliteType.Text);
        var pFn   = cmd.Parameters.Add("$fn",   Microsoft.Data.Sqlite.SqliteType.Text);
        pRead.Value = isRead ? 1 : 0;
        foreach (var (accountId, folderName, messageId) in materialized)
        {
            pUid.Value = messageId;
            pAid.Value = accountId.ToString();
            pFn.Value  = folderName;
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var (accountId, folderName) in materialized
                     .Select(i => (i.AccountId, i.FolderName)).Distinct())
            await UpdateFolderCountsAsync(conn, (Microsoft.Data.Sqlite.SqliteTransaction)tx,
                accountId, folderName, CancellationToken.None);
        await tx.CommitAsync();
    }

    public async Task UpdateFlagIdAsync(Guid accountId, string folderName, string messageId, string? flagId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE MessageSummary SET flag_id=$fid " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$fid", (object?)flagId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uid", messageId);
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateFlagIdBatchAsync(
        IEnumerable<(Guid AccountId, string FolderName, string MessageId)> items,
        string? flagId)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
        cmd.CommandText =
            "UPDATE MessageSummary SET flag_id=$fid " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        var pFid = cmd.Parameters.Add("$fid", SqliteType.Text);
        var pUid = cmd.Parameters.Add("$uid", SqliteType.Text);
        var pAid = cmd.Parameters.Add("$aid", SqliteType.Text);
        var pFn  = cmd.Parameters.Add("$fn",  SqliteType.Text);
        pFid.Value = (object?)flagId ?? DBNull.Value;
        foreach (var (accountId, folderName, messageId) in items)
        {
            pUid.Value = messageId;
            pAid.Value = accountId.ToString();
            pFn.Value  = folderName;
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task UpdatePreviewAsync(Guid accountId, string folderName, string messageId, string preview)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE MessageSummary SET preview_text=$preview " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$preview", preview);
        cmd.Parameters.AddWithValue("$uid",     messageId);
        cmd.Parameters.AddWithValue("$aid",     accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",      folderName);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdatePreviewsBatchAsync(
        Guid accountId, string folderName, IEnumerable<(string MessageId, string Preview)> updates)
    {
        var list = updates as IList<(string, string)> ?? updates.ToList();
        if (list.Count == 0) return;

        await using var conn = await OpenAsync();
        await using var tx   = await conn.BeginTransactionAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE MessageSummary SET preview_text=$preview " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        var pPrev = cmd.Parameters.Add("$preview", SqliteType.Text);
        var pUid  = cmd.Parameters.Add("$uid",     SqliteType.Text);
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);

        foreach (var (messageId, preview) in list)
        {
            pPrev.Value = preview;
            pUid.Value  = messageId;
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public async Task UpsertDetailAsync(MailMessageDetail detail)
    {
        if (string.IsNullOrWhiteSpace(detail.Bcc))
            detail.Bcc = ExtractHeaderValue(detail.RawHeaders, "Bcc");
        var attJson = detail.Attachments.Count > 0
            ? JsonSerializer.Serialize(detail.Attachments.Select(a => new { a.FileName, a.ContentType, a.FileSize, a.PartSpecifier, a.ContentId, a.IsInline }))
            : null;

        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MessageDetail(unique_id, account_id, folder_name, to_addr, cc, bcc, reply_to, plain_body, html_body, attachments_json, calendar_ics, raw_headers, draft_compose_mode, draft_spell_language)
            VALUES($uid, $aid, $fn, $to, $cc, $bcc, $rt, $plain, $html, $attjson, $ics, $headers, $mode, $language)
            ON CONFLICT(unique_id, account_id, folder_name) DO UPDATE SET
                to_addr          = excluded.to_addr,
                cc               = excluded.cc,
                bcc              = excluded.bcc,
                reply_to         = excluded.reply_to,
                plain_body       = excluded.plain_body,
                html_body        = excluded.html_body,
                attachments_json = excluded.attachments_json,
                calendar_ics     = excluded.calendar_ics,
                raw_headers      = excluded.raw_headers,
                draft_compose_mode = excluded.draft_compose_mode,
                draft_spell_language = excluded.draft_spell_language;
            """;
        cmd.Parameters.AddWithValue("$uid",    detail.MessageId);
        cmd.Parameters.AddWithValue("$aid",    detail.AccountId.ToString());
        cmd.Parameters.AddWithValue("$fn",     detail.FolderName);
        cmd.Parameters.AddWithValue("$to",     detail.To);
        cmd.Parameters.AddWithValue("$cc",     detail.Cc);
        cmd.Parameters.AddWithValue("$bcc",    detail.Bcc);
        cmd.Parameters.AddWithValue("$rt",     detail.ReplyTo);
        cmd.Parameters.AddWithValue("$plain",  detail.PlainTextBody);
        cmd.Parameters.AddWithValue("$html",   detail.HtmlBody);
        cmd.Parameters.AddWithValue("$attjson", (object?)attJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ics",    (object?)detail.CalendarIcs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$headers", detail.RawHeaders ?? string.Empty);
        cmd.Parameters.AddWithValue("$mode", (int)detail.DraftComposeMode);
        cmd.Parameters.AddWithValue("$language", detail.DraftSpellLanguage ?? string.Empty);
        await cmd.ExecuteNonQueryAsync();

        // Update the summary's has_attachments flag
        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText =
            "UPDATE MessageSummary SET has_attachments=$ha " +
            "WHERE unique_id=$uid AND account_id=$aid AND folder_name=$fn;";
        cmd2.Parameters.AddWithValue("$ha", detail.Attachments.Any(a => !a.IsInline) ? 1 : 0);
        cmd2.Parameters.AddWithValue("$uid", detail.MessageId);
        cmd2.Parameters.AddWithValue("$aid", detail.AccountId.ToString());
        cmd2.Parameters.AddWithValue("$fn",  detail.FolderName);
        await cmd2.ExecuteNonQueryAsync();

        // Replace the summary-only preview in FTS with the complete cached body. The INSERT path
        // also repairs legacy IMAP rows that predate incremental summary indexing.
        await using var cmd3 = conn.CreateCommand();
        cmd3.CommandText = """
            UPDATE LocalMessageFts
               SET to_addr=$to,cc_addr=$cc,body_text=$body
             WHERE rowid IN (SELECT fts_rowid FROM LocalMessageFtsKey
                              WHERE account_id=$aid AND unique_id=$uid AND folder_name=$fn);
            INSERT INTO LocalMessageFts
                (account_id,unique_id,folder_name,from_addr,to_addr,cc_addr,subject,body_text)
                SELECT s.account_id,s.unique_id,s.folder_name,s.from_disp,$to,$cc,s.subject,$body
                  FROM MessageSummary s
                 WHERE s.account_id=$aid AND s.unique_id=$uid AND s.folder_name=$fn
                   AND NOT EXISTS (SELECT 1 FROM LocalMessageFtsKey
                                    WHERE account_id=$aid AND unique_id=$uid AND folder_name=$fn);
            INSERT INTO LocalMessageFtsKey(account_id,unique_id,folder_name,fts_rowid)
                SELECT $aid,$uid,$fn,last_insert_rowid()
                 WHERE changes()=1 AND NOT EXISTS (SELECT 1 FROM LocalMessageFtsKey
                                                   WHERE account_id=$aid AND unique_id=$uid AND folder_name=$fn);
            """;
        cmd3.Parameters.AddWithValue("$uid", detail.MessageId);
        cmd3.Parameters.AddWithValue("$aid", detail.AccountId.ToString());
        cmd3.Parameters.AddWithValue("$fn", detail.FolderName);
        cmd3.Parameters.AddWithValue("$to", detail.To ?? string.Empty);
        cmd3.Parameters.AddWithValue("$cc", detail.Cc ?? string.Empty);
        cmd3.Parameters.AddWithValue("$body", string.IsNullOrWhiteSpace(detail.PlainTextBody)
            ? detail.HtmlBody ?? string.Empty : detail.PlainTextBody);
        await cmd3.ExecuteNonQueryAsync();

        await tx.CommitAsync();
    }

    public async Task<MailMessageDetail?> LoadDetailAsync(Guid accountId, string folderName, string messageId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        // LEFT JOIN so the detail can be loaded even when the MessageSummary row is
        // missing (e.g. the message was purged from the sync range or deleted from the
        // server, but the cached body + calendar ICS remain). Without this, opening a
        // calendar event's source invite fails with "message not found" because the
        // INNER JOIN returns nothing.
        cmd.CommandText = """
            SELECT d.to_addr, d.cc, d.bcc, d.reply_to, d.plain_body, d.html_body,
                   s.from_disp, s.subject, s.date_ticks, s.is_read, d.attachments_json, d.calendar_ics, d.raw_headers,
                   d.draft_compose_mode, d.draft_spell_language, s.message_direction
            FROM MessageDetail d
            LEFT JOIN MessageSummary s USING (unique_id, account_id, folder_name)
            WHERE d.unique_id=$uid AND d.account_id=$aid AND d.folder_name=$fn;
            """;
        cmd.Parameters.AddWithValue("$uid", messageId);
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        List<AttachmentModel> attachments = [];
        if (!r.IsDBNull(10))
        {
            var json = r.GetString(10);
            try
            {
                var metas = JsonSerializer.Deserialize<List<AttachmentMeta>>(json);
                if (metas != null)
                    attachments = metas.Select(m => new AttachmentModel
                    {
                        FileName      = m.FileName,
                        ContentType   = m.ContentType,
                        FileSize      = m.FileSize,
                        PartSpecifier = m.PartSpecifier,
                        ContentId     = m.ContentId,
                        IsInline      = m.IsInline,
                    }).ToList();
            }
            catch { /* corrupt json — ignore */ }
        }

        var calendarIcs = r.IsDBNull(11) ? string.Empty : r.GetString(11);
        var storedComposeMode = !r.IsDBNull(13) && Enum.IsDefined(typeof(ComposeMode), r.GetInt32(13))
            ? (ComposeMode)r.GetInt32(13)
            : ComposeMode.PlainText;
        // Drafts saved by builds predating draft_compose_mode have the default zero even when
        // html_body contains the authoritative editor document. Recover those existing drafts.
        if (storedComposeMode == ComposeMode.PlainText && !string.IsNullOrWhiteSpace(r.GetString(5)))
            storedComposeMode = ComposeMode.Html;

        var rawHeaders = r.IsDBNull(12) ? string.Empty : r.GetString(12);
        var bcc = r.IsDBNull(2) ? string.Empty : r.GetString(2);
        if (string.IsNullOrWhiteSpace(bcc)) bcc = ExtractHeaderValue(rawHeaders, "Bcc");

        return new MailMessageDetail
        {
            MessageId     = messageId,
            AccountId     = accountId,
            FolderName    = folderName,
            To            = r.GetString(0),
            Cc            = r.GetString(1),
            Bcc           = bcc,
            ReplyTo       = r.GetString(3),
            PlainTextBody = r.GetString(4),
            HtmlBody      = r.GetString(5),
            From          = r.IsDBNull(6) ? string.Empty : r.GetString(6),
            Subject       = r.IsDBNull(7) ? "(no subject)" : r.GetString(7),
            Date          = r.IsDBNull(8) ? DateTimeOffset.MinValue : new DateTimeOffset(r.GetInt64(8), TimeSpan.Zero),
            IsRead        = !r.IsDBNull(9) && r.GetInt64(9) != 0,
            Attachments   = attachments,
            CalendarIcs   = calendarIcs,
            RawHeaders    = rawHeaders,
            DraftComposeMode = storedComposeMode,
            DraftSpellLanguage = r.IsDBNull(14) ? string.Empty : r.GetString(14),
            Direction      = r.IsDBNull(15) ? MessageDirection.Unknown : (MessageDirection)r.GetInt64(15),
            CalendarInvite = string.IsNullOrWhiteSpace(calendarIcs) ? null : IcsModel.Parse(calendarIcs),
        };
    }

    public async Task<IReadOnlyDictionary<string, RuleMatchData>> LoadRuleMatchDataAsync(
        Guid accountId, string folderName, IReadOnlyCollection<string> messageIds,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0)
            return new Dictionary<string, RuleMatchData>(StringComparer.Ordinal);

        var wanted = messageIds.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, RuleMatchData>(wanted.Count, StringComparer.Ordinal);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Reading the folder slice once is substantially cheaper than issuing one SELECT for every
        // row when a rule includes BODY or "Also CC/BCC". Filtering the bounded result in memory
        // also avoids SQLite's parameter-count limit for large folders.
        cmd.CommandText = """
            SELECT unique_id, cc, bcc, plain_body, html_body, raw_headers
              FROM MessageDetail
             WHERE account_id=$aid AND folder_name=$fn;
            """;
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn", folderName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var messageId = reader.GetString(0);
            if (!wanted.Contains(messageId)) continue;
            var plain = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var html = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            var bcc = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            if (string.IsNullOrWhiteSpace(bcc))
                bcc = ExtractHeaderValue(reader.IsDBNull(5) ? string.Empty : reader.GetString(5), "Bcc");
            result[messageId] = new RuleMatchData(
                string.IsNullOrWhiteSpace(plain) ? html : plain,
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                bcc);
        }
        return result;
    }

    private static string ExtractHeaderValue(string? rawHeaders, string headerName)
    {
        if (string.IsNullOrWhiteSpace(rawHeaders)) return string.Empty;
        var match = Regex.Match(rawHeaders,
            $@"(?im)^{Regex.Escape(headerName)}[ \t]*:[ \t]*(?<value>[^\r\n]*(?:\r?\n[ \t]+[^\r\n]*)*)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        return !match.Success ? string.Empty : Regex.Replace(match.Groups["value"].Value,
            @"\r?\n[ \t]+", " ").Trim();
    }

    public async Task<HashSet<string>> GetAllMessageIdsAsync(Guid accountId, string folderName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        var result = new HashSet<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            result.Add(r.GetString(0));
        return result;
    }

    public async Task<Dictionary<string, bool>> LoadFolderReadStatesAsync(Guid accountId, string folderName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unique_id, is_read FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        var result = new Dictionary<string, bool>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            result[r.GetString(0)] = r.GetInt64(1) != 0;
        return result;
    }

    /// <summary>
    /// Which of <paramref name="messageIds"/> already exist in the folder — a bounded
    /// <c>WHERE unique_id IN (…)</c> so a live sync can dedupe its small fetched batch without
    /// scanning (and materialising a HashSet of) every id in a large cached folder.
    /// </summary>
    public async Task<HashSet<string>> GetExistingMessageIdsAsync(
        Guid accountId, string folderName, IEnumerable<string> messageIds)
    {
        var ids = messageIds as IReadOnlyList<string> ?? messageIds.ToList();
        var result = new HashSet<string>();
        if (ids.Count == 0) return result;

        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        var placeholders = string.Join(",", ids.Select((_, i) => "$id" + i));
        cmd.CommandText =
            $"SELECT unique_id FROM MessageSummary WHERE account_id=$aid AND folder_name=$fn AND unique_id IN ({placeholders});";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        for (var i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue("$id" + i, ids[i]);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            result.Add(r.GetString(0));
        return result;
    }

    public async Task<string> GetMaxMessageKeyAsync(Guid accountId, string folderName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        // IMAP stores plain decimal UID strings; compute the numeric high-water mark via CAST so
        // "9" < "10" sorts correctly (lexicographic MAX would not). Graph rows are non-numeric and
        // CAST to 0 — harmless, since Graph never reads this value.
        cmd.CommandText =
            "SELECT COALESCE(MAX(CAST(unique_id AS INTEGER)), 0) FROM MessageSummary " +
            "WHERE account_id=$aid AND folder_name=$fn;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn",  folderName);
        var result = await cmd.ExecuteScalarAsync();
        var max = result is long l ? l : 0L;
        return max.ToString(CultureInfo.InvariantCulture);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static async Task<List<MailMessageSummary>> ReadSummariesAsync(SqliteCommand cmd)
    {
        var list = new List<MailMessageSummary>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new MailMessageSummary
            {
                MessageId   = r.GetString(0),
                AccountId   = Guid.Parse(r.GetString(1)),
                FolderName  = r.GetString(2),
                From        = r.GetString(3),
                To          = r.GetString(4),
                Subject     = r.GetString(5),
                Date        = new DateTimeOffset(r.GetInt64(6), TimeSpan.Zero),
                IsRead      = r.GetInt64(7) != 0,
                Preview     = r.GetString(8),
                IsReplied      = r.GetInt64(9) != 0,
                IsForwarded    = r.GetInt64(10) != 0,
                HasAttachments = r.GetInt64(11) != 0,
                IsMailingList  = r.GetInt64(12) != 0,
                FlagId         = r.IsDBNull(13) ? null : r.GetString(13),
                InternetMessageId = r.IsDBNull(14) ? string.Empty : r.GetString(14),
                Direction     = r.IsDBNull(15) ? MessageDirection.Unknown : (MessageDirection)r.GetInt64(15),
            });
        }
        return list;
    }

    public async Task<int> CountSummariesAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM MessageSummary WHERE account_id = @id";
        cmd.Parameters.AddWithValue("@id", accountId.ToString());
        var result = await cmd.ExecuteScalarAsync();
        return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
    }

    public async Task<Dictionary<string, int>> CountSummariesByFolderAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT folder_name, COUNT(*) FROM MessageSummary WHERE account_id = @id GROUP BY folder_name";
        cmd.Parameters.AddWithValue("@id", accountId.ToString());
        var byFolder = new Dictionary<string, int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            byFolder[reader.GetString(0)] = reader.GetInt32(1);
        return byFolder;
    }

    public async Task<DateTimeOffset?> GetOldestMessageDateAsync(Guid accountId)
    {
        await using var conn = await OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(date_ticks) FROM MessageSummary WHERE account_id = @id";
        cmd.Parameters.AddWithValue("@id", accountId.ToString());
        var result = await cmd.ExecuteScalarAsync();
        if (result == null || result == DBNull.Value) return null;
        var ticks = Convert.ToInt64(result);
        return ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var c = new SqliteConnection(_connectionString);
        await c.OpenAsync();
        return c;
    }

    /// <summary>Lightweight DTO for serializing attachment metadata to JSON (no Content bytes).</summary>
    private sealed record AttachmentMeta(
        string  FileName,
        string  ContentType,
        long    FileSize,
        string? PartSpecifier,
        string? ContentId = null,
        bool IsInline = false);

    // ── Calendar events ──────────────────────────────────────────────────────────

    public async Task UpsertCalendarEventAsync(CalendarEvent evt)
    {
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CalendarEvent(uid, account_id, summary, description, location,
                                      organizer, organizer_name, start_time_ticks, end_time_ticks,
                                      sequence, method, source_message_id, source_folder, response_status,
                                      is_all_day, recurrence_rule, exdates, is_graph, calendar_id, calendar_name,
                                      resource_url)
            VALUES($uid, $aid, $sum, $desc, $loc, $org, $orgn, $st, $et, $seq, $meth, $smid, $sf, $rs, $allday, $rrule, $exd, $graph, $calid, $calname, $resurl)
            ON CONFLICT(uid, account_id) DO UPDATE SET
                summary           = excluded.summary,
                description       = excluded.description,
                location          = excluded.location,
                organizer         = excluded.organizer,
                organizer_name    = excluded.organizer_name,
                start_time_ticks  = excluded.start_time_ticks,
                end_time_ticks    = excluded.end_time_ticks,
                sequence          = excluded.sequence,
                method            = excluded.method,
                source_message_id = excluded.source_message_id,
                source_folder     = excluded.source_folder,
                is_all_day        = excluded.is_all_day,
                recurrence_rule   = excluded.recurrence_rule,
                exdates           = excluded.exdates,
                -- Preserve an existing calendar tag when the incoming row is untagged (e.g. a server
                -- write-back that targets the default calendar and carries no tag); the next full
                -- sync re-tags it. A non-empty incoming tag always wins.
                calendar_id       = CASE WHEN excluded.calendar_id   = '' THEN calendar_id   ELSE excluded.calendar_id   END,
                calendar_name     = CASE WHEN excluded.calendar_name = '' THEN calendar_name ELSE excluded.calendar_name END,
                -- Preserve a stored CalDAV resource href when the incoming row carries none (a local
                -- create/edit write-back has no href until the next read-sync captures the real one).
                resource_url      = CASE WHEN excluded.resource_url  = '' THEN resource_url  ELSE excluded.resource_url  END
            WHERE CalendarEvent.is_graph = 0 OR excluded.is_graph = 1;
            """;
        // The DO UPDATE ... WHERE guard: server-synced rows (is_graph=1) are owned by the calendar
        // sync service; the invite harvest and local authoring paths (which write is_graph=0) must
        // never overwrite them on a UID collision. The sync service's own write-back stores the
        // server's returned copy WITH is_graph=1 (excluded.is_graph=1), which must update the row —
        // without that clause a successful server edit left the local copy stale until the next
        // replace-slice sync.
        cmd.Parameters.AddWithValue("$uid",  evt.Uid);
        cmd.Parameters.AddWithValue("$aid",  evt.AccountId.ToString());
        cmd.Parameters.AddWithValue("$sum",  evt.Summary);
        cmd.Parameters.AddWithValue("$desc", evt.Description);
        cmd.Parameters.AddWithValue("$loc",  evt.Location);
        cmd.Parameters.AddWithValue("$org",  evt.Organizer);
        cmd.Parameters.AddWithValue("$orgn", evt.OrganizerName);
        cmd.Parameters.AddWithValue("$st",   (object?)evt.StartTimeTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$et",   (object?)evt.EndTimeTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seq",  (object?)evt.Sequence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$meth", (object?)evt.Method ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$smid", evt.SourceMessageId);
        cmd.Parameters.AddWithValue("$sf",   evt.SourceFolder);
        cmd.Parameters.AddWithValue("$rs",   (int)evt.ResponseStatus);
        cmd.Parameters.AddWithValue("$allday", evt.IsAllDay ? 1 : 0);
        cmd.Parameters.AddWithValue("$rrule", (object?)evt.RecurrenceRule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exd",  (object?)evt.ExDates ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$graph", evt.IsGraph ? 1 : 0);
        cmd.Parameters.AddWithValue("$calid", evt.CalendarId);
        cmd.Parameters.AddWithValue("$calname", evt.CalendarName);
        cmd.Parameters.AddWithValue("$resurl", evt.ResourceUrl);
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public async Task ReplaceGraphCalendarEventsAsync(Guid accountId, IReadOnlyList<CalendarEvent> events)
    {
        // Replace-slice semantics (v1, no delta tokens): each sync deletes the account's previous
        // Graph-sourced rows and inserts the fresh window, all in one transaction, so events that
        // vanished on the server disappear locally. Harvested-invite and local rows (is_graph=0)
        // are untouched.
        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM CalendarEvent WHERE account_id = $aid AND is_graph = 1;";
            del.Parameters.AddWithValue("$aid", accountId.ToString());
            await del.ExecuteNonQueryAsync();
        }

        await using (var ins = conn.CreateCommand())
        {
            // INSERT OR REPLACE: defensive against a duplicate occurrence id within one payload,
            // and against a (astronomically unlikely) collision with a harvested invite's UID —
            // the Graph copy wins for the (uid, account) key.
            ins.CommandText = """
                INSERT OR REPLACE INTO CalendarEvent(uid, account_id, summary, description, location,
                                          organizer, organizer_name, start_time_ticks, end_time_ticks,
                                          sequence, method, source_message_id, source_folder, response_status,
                                          is_all_day, recurrence_rule, exdates, is_graph, calendar_id, calendar_name,
                                          resource_url)
                VALUES($uid, $aid, $sum, $desc, $loc, $org, $orgn, $st, $et, $seq, $meth, $smid, $sf, $rs, $allday, $rrule, $exd, 1, $calid, $calname, $resurl);
                """;
            var pUid  = ins.Parameters.Add("$uid",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pAid  = ins.Parameters.Add("$aid",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pSum  = ins.Parameters.Add("$sum",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pDesc = ins.Parameters.Add("$desc", Microsoft.Data.Sqlite.SqliteType.Text);
            var pLoc  = ins.Parameters.Add("$loc",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pOrg  = ins.Parameters.Add("$org",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pOrgn = ins.Parameters.Add("$orgn", Microsoft.Data.Sqlite.SqliteType.Text);
            var pSt   = ins.Parameters.Add("$st",   Microsoft.Data.Sqlite.SqliteType.Integer);
            var pEt   = ins.Parameters.Add("$et",   Microsoft.Data.Sqlite.SqliteType.Integer);
            var pSeq  = ins.Parameters.Add("$seq",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pMeth = ins.Parameters.Add("$meth", Microsoft.Data.Sqlite.SqliteType.Text);
            var pSmid = ins.Parameters.Add("$smid", Microsoft.Data.Sqlite.SqliteType.Text);
            var pSf   = ins.Parameters.Add("$sf",   Microsoft.Data.Sqlite.SqliteType.Text);
            var pRs   = ins.Parameters.Add("$rs",   Microsoft.Data.Sqlite.SqliteType.Integer);
            var pAll  = ins.Parameters.Add("$allday", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pRr   = ins.Parameters.Add("$rrule", Microsoft.Data.Sqlite.SqliteType.Text);
            var pExd  = ins.Parameters.Add("$exd",  Microsoft.Data.Sqlite.SqliteType.Text);
            var pCid  = ins.Parameters.Add("$calid",   Microsoft.Data.Sqlite.SqliteType.Text);
            var pCn   = ins.Parameters.Add("$calname", Microsoft.Data.Sqlite.SqliteType.Text);
            var pRes  = ins.Parameters.Add("$resurl",  Microsoft.Data.Sqlite.SqliteType.Text);

            foreach (var evt in events)
            {
                pUid.Value  = evt.Uid;
                pAid.Value  = accountId.ToString();
                pSum.Value  = evt.Summary;
                pDesc.Value = evt.Description;
                pLoc.Value  = evt.Location;
                pOrg.Value  = evt.Organizer;
                pOrgn.Value = evt.OrganizerName;
                pSt.Value   = (object?)evt.StartTimeTicks ?? DBNull.Value;
                pEt.Value   = (object?)evt.EndTimeTicks ?? DBNull.Value;
                pSeq.Value  = (object?)evt.Sequence ?? DBNull.Value;
                pMeth.Value = (object?)evt.Method ?? DBNull.Value;
                pSmid.Value = evt.SourceMessageId;
                pSf.Value   = evt.SourceFolder;
                pRs.Value   = (int)evt.ResponseStatus;
                pAll.Value  = evt.IsAllDay ? 1 : 0;
                pRr.Value   = (object?)evt.RecurrenceRule ?? DBNull.Value;
                pExd.Value  = (object?)evt.ExDates ?? DBNull.Value;
                pCid.Value  = evt.CalendarId;
                pCn.Value   = evt.CalendarName;
                pRes.Value  = evt.ResourceUrl;
                await ins.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
    }

    /// <summary>Updates the user-editable local subject/body without altering envelope data,
    /// headers, dates, attachments or embedded-resource references.</summary>
    public async Task UpdateStoredMessageContentAsync(Guid accountId, string folderName,
        string messageId, string subject, string plainBody, string? htmlBody, ComposeMode mode)
    {
        var preview = string.Join(' ', (plainBody ?? string.Empty)
            .Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (preview.Length > 240) preview = preview[..240];

        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            UPDATE MessageSummary SET subject=$subject, preview_text=$preview
             WHERE account_id=$aid AND folder_name=$fn AND unique_id=$uid;
            UPDATE MessageDetail SET plain_body=$plain, html_body=$html, draft_compose_mode=$mode
             WHERE account_id=$aid AND folder_name=$fn AND unique_id=$uid;
            UPDATE LocalMessageFts SET subject=$subject, body_text=$body
             WHERE rowid IN (SELECT fts_rowid FROM LocalMessageFtsKey
                              WHERE account_id=$aid AND folder_name=$fn AND unique_id=$uid);
            """;
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fn", folderName);
        cmd.Parameters.AddWithValue("$uid", messageId);
        cmd.Parameters.AddWithValue("$subject", subject ?? string.Empty);
        cmd.Parameters.AddWithValue("$preview", preview);
        cmd.Parameters.AddWithValue("$plain", plainBody ?? string.Empty);
        cmd.Parameters.AddWithValue("$html", htmlBody ?? string.Empty);
        cmd.Parameters.AddWithValue("$body", string.IsNullOrWhiteSpace(plainBody) ? htmlBody ?? string.Empty : plainBody);
        cmd.Parameters.AddWithValue("$mode", (int)mode);
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public async Task DeleteGraphCalendarEventsInRangeAsync(Guid accountId, DateTime startUtc, DateTime endUtc)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM CalendarEvent
             WHERE account_id = $aid AND is_graph = 1
               AND start_time_ticks < $end
               AND COALESCE(end_time_ticks, start_time_ticks) >= $start;
            """;
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$start", startUtc.Ticks);
        cmd.Parameters.AddWithValue("$end", endUtc.Ticks);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<CalendarEvent>> LoadCalendarEventsAsync()
    {
        var list = new List<CalendarEvent>();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT uid, account_id, summary, description, location, organizer, organizer_name,
                   start_time_ticks, end_time_ticks, sequence, method, source_message_id,
                   source_folder, response_status, is_all_day, recurrence_rule, exdates, is_graph,
                   calendar_id, calendar_name, resource_url
            FROM CalendarEvent
            ORDER BY start_time_ticks IS NULL, start_time_ticks ASC;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new CalendarEvent
            {
                Uid              = r.GetString(0),
                AccountId        = Guid.Parse(r.GetString(1)),
                Summary          = r.GetString(2),
                Description      = r.GetString(3),
                Location         = r.GetString(4),
                Organizer        = r.GetString(5),
                OrganizerName    = r.GetString(6),
                StartTimeTicks   = r.IsDBNull(7) ? null : r.GetInt64(7),
                EndTimeTicks     = r.IsDBNull(8) ? null : r.GetInt64(8),
                Sequence         = r.IsDBNull(9) ? null : r.GetString(9),
                Method           = r.IsDBNull(10) ? null : r.GetString(10),
                SourceMessageId  = r.GetString(11),
                SourceFolder     = r.GetString(12),
                ResponseStatus   = (CalendarResponseStatus)r.GetInt32(13),
                IsAllDay         = !r.IsDBNull(14) && r.GetInt32(14) != 0,
                RecurrenceRule   = r.IsDBNull(15) ? null : r.GetString(15),
                ExDates          = r.IsDBNull(16) ? null : r.GetString(16),
                IsGraph          = !r.IsDBNull(17) && r.GetInt32(17) != 0,
                CalendarId       = r.IsDBNull(18) ? string.Empty : r.GetString(18),
                CalendarName     = r.IsDBNull(19) ? string.Empty : r.GetString(19),
                ResourceUrl      = r.IsDBNull(20) ? string.Empty : r.GetString(20),
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<(Guid AccountId, string CalendarId, string CalendarName)>> LoadCalendarSourcesAsync()
    {
        var list = new List<(Guid, string, string)>();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Distinct server calendars across all synced rows — one row per (account, calendar), used to
        // build the per-calendar grandchild nodes under each account in the folder tree.
        cmd.CommandText = """
            SELECT DISTINCT account_id, calendar_id, calendar_name
            FROM CalendarEvent
            WHERE is_graph = 1 AND calendar_id <> ''
            ORDER BY calendar_name;
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add((Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2)));
        return list;
    }

    public async Task UpdateCalendarResponseStatusAsync(string uid, Guid accountId, CalendarResponseStatus status)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE CalendarEvent SET response_status=$rs WHERE uid=$uid AND account_id=$aid;";
        cmd.Parameters.AddWithValue("$rs",  (int)status);
        cmd.Parameters.AddWithValue("$uid", uid);
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteCalendarEventAsync(string uid, Guid accountId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM CalendarEvent WHERE uid=$uid AND account_id=$aid;";
        cmd.Parameters.AddWithValue("$uid", uid);
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<(Guid AccountId, string FolderName, string MessageId, string IcsText)>> LoadAllCalendarIcsAsync()
    {
        var list = new List<(Guid, string, string, string)>();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT account_id, folder_name, unique_id, calendar_ics
            FROM MessageDetail
            WHERE calendar_ics IS NOT NULL AND calendar_ics != '';
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add((
                Guid.Parse(r.GetString(0)),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3)));
        }
        return list;
    }

    public async Task ClearOrphanedCalendarSourceLinksAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Clear source_message_id and source_folder on any CalendarEvent whose
        // source MessageDetail row no longer exists.  This happens when the local
        // message cache purges old messages (sync window rolls forward or the user
        // deletes a message) but the CalendarEvent outlives it.  After clearing,
        // OpenSourceMessage silently no-ops for these events instead of failing with
        // "Message UID N not found".
        // is_graph = 0 guard: Graph-synced rows never reference an invite email (their source
        // fields are already empty) and are owned by the sync's replace-slice — the invite
        // harvest's cleanup must not touch them.
        cmd.CommandText = """
            UPDATE CalendarEvent
            SET source_message_id = '', source_folder = ''
            WHERE source_message_id != ''
              AND is_graph = 0
              AND NOT EXISTS (
                  SELECT 1 FROM MessageDetail md
                  WHERE md.unique_id   = CalendarEvent.source_message_id
                    AND md.account_id  = CalendarEvent.account_id
                    AND md.folder_name = CalendarEvent.source_folder
              );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Graph delta cursors (PR 7b) ──────────────────────────────────────────────

    public async Task<string?> GetDeltaTokenAsync(Guid accountId, string folderId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT delta_token FROM DeltaToken WHERE account_id=$aid AND folder_id=$fid;";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$fid", folderId);
        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    public async Task SetDeltaTokenAsync(Guid accountId, string folderId, string deltaToken)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DeltaToken(account_id, folder_id, delta_token, updated_utc)
            VALUES($aid, $fid, $token, $now)
            ON CONFLICT(account_id, folder_id) DO UPDATE SET
                delta_token = excluded.delta_token,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$aid",   accountId.ToString());
        cmd.Parameters.AddWithValue("$fid",   folderId);
        cmd.Parameters.AddWithValue("$token", deltaToken);
        cmd.Parameters.AddWithValue("$now",   DateTime.UtcNow.Ticks);
        await cmd.ExecuteNonQueryAsync();
    }
}
