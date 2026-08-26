using System.IO;
using Microsoft.Data.Sqlite;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class LocalFolderTreeMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qm-folder-tree-" + Guid.NewGuid());
    private string Database => Path.Combine(_directory, "mail.db");

    [Fact]
    public void ShadowMigration_MergesSystemInboxAliasesButPreservesUnderscoredContainers()
    {
        Directory.CreateDirectory(_directory);
        var root = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Folder(account_id TEXT,full_name TEXT,display_name TEXT,kind INTEGER,is_container INTEGER);
                CREATE TABLE MessageSummary(unique_id TEXT,account_id TEXT,folder_name TEXT);
                INSERT INTO Folder VALUES($a,'In','In',1,0),($a,'_In','_In',0,1),
                    ($a,'_In/Customers','Customers',0,0),($b,'Inbox','Inbox',1,0);
                INSERT INTO MessageSummary VALUES('1',$a,'In'),('2',$a,'_In/Customers'),('3',$b,'Inbox');
                """;
            command.Parameters.AddWithValue("$a", first.ToString("D"));
            command.Parameters.AddWithValue("$b", second.ToString("D"));
            command.ExecuteNonQuery();
        }
        var accounts = new[]
        {
            new AccountModel { Id = first, FolderTreeRootId = root, FolderTreeRootName = "Eudora", IsActive = true, BackendKind = BackendKind.Pop3Smtp },
            new AccountModel { Id = second, FolderTreeRootId = root, FolderTreeRootName = "Eudora", IsActive = true, BackendKind = BackendKind.Pop3Smtp },
        };

        var result = new LocalFolderTreeMigrationService().BuildShadowTree(Database, accounts);

        Assert.Equal(3, result.Messages);
        Assert.Equal(3, result.MappedMessages);
        Assert.Equal(0, result.UnmappedMessages);
        using var verify = new SqliteConnection($"Data Source={Database};Mode=ReadOnly;Pooling=False");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = """
            SELECT
              (SELECT COUNT(DISTINCT folder_id) FROM LocalFolderBinding_shadow
                WHERE legacy_full_name IN ('In','Inbox')),
              (SELECT COUNT(*) FROM LocalFolderNode_shadow WHERE canonical_path='_In'),
              (SELECT COUNT(*) FROM LocalFolderNode_shadow WHERE canonical_path='_In/Customers'),
              (SELECT COUNT(*) FROM LocalFolderNode_shadow WHERE canonical_path='');
            """;
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
        Assert.Equal(1, reader.GetInt64(3));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
