using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class QuickMailProfileImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"EQM-QM-Import-{Guid.NewGuid():N}");

    [Fact]
    public async Task ImportAsync_ClonesProfileDatabaseAndStartsOffline()
    {
        var source = Path.Combine(_root, "QuickMail");
        var destination = Path.Combine(_root, "Eudora QuickMail");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "accounts.json"), "[{\"Id\":\"00000000-0000-0000-0000-000000000001\"}]");
        File.WriteAllText(Path.Combine(source, "contacts.json"), "[{\"DisplayName\":\"Fixture Contact\"}]");
        File.WriteAllText(Path.Combine(source, "quickmail.log"), "must not be imported");
        Directory.CreateDirectory(Path.Combine(source, "themes"));
        File.WriteAllText(Path.Combine(source, "themes", "fixture.json"), "{}");

        var sourceDatabase = Path.Combine(source, "mail.db");
        using var writer = Open(sourceDatabase);
        Execute(writer, "PRAGMA journal_mode=WAL;");
        Execute(writer, "PRAGMA user_version=5;");
        Execute(writer, "CREATE TABLE MessageDetail (id TEXT PRIMARY KEY, attachments_json TEXT, mime_bytes BLOB);");
        using (var insert = writer.CreateCommand())
        {
            insert.CommandText = "INSERT INTO MessageDetail VALUES ('pop3-1', $json, $mime);";
            insert.Parameters.AddWithValue("$json", "[{\"FileName\":\"fixture.txt\",\"PartSpecifier\":\"0\"}]");
            insert.Parameters.AddWithValue("$mime", new byte[] { 1, 2, 3, 4, 5 });
            insert.ExecuteNonQuery();
        }

        var result = await new QuickMailProfileImporter().ImportAsync(
            new QuickMailProfileImportOptions(source, destination));

        Assert.Equal(5, result.SourceSchemaVersion);
        Assert.True(File.Exists(Path.Combine(destination, "accounts.json")));
        Assert.True(File.Exists(Path.Combine(destination, "contacts.json")));
        Assert.True(File.Exists(Path.Combine(destination, "themes", "fixture.json")));
        Assert.False(File.Exists(Path.Combine(destination, "quickmail.log")));
        Assert.True(File.Exists(Path.Combine(destination, "quickmail-profile-import.json")));

        using var imported = Open(Path.Combine(destination, "mail.db"), readOnly: true);
        using var command = imported.CreateCommand();
        command.CommandText = "SELECT attachments_json, mime_bytes FROM MessageDetail WHERE id='pop3-1';";
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Contains("fixture.txt", row.GetString(0));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, (byte[])row[1]);

        using var activity = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, "mail-activity.json")));
        Assert.Equal(1, activity.RootElement.GetProperty("Mode").GetInt32());
        Assert.True(File.Exists(sourceDatabase));
        Assert.False(File.Exists(Path.Combine(source, "mail-activity.json")));
    }

    [Fact]
    public async Task ImportAsync_RejectsFutureQuickMailSchema()
    {
        var source = CreateMinimalProfile("Future", QuickMailProfileImporter.MaxSupportedQuickMailSchemaVersion + 1);
        var destination = Path.Combine(_root, "Destination");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new QuickMailProfileImporter().ImportAsync(new(source, destination)));

        Assert.Contains("currently supports QuickMail schemas", error.Message);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task ImportAsync_RejectsNonEmptyDestinationWithoutChangingIt()
    {
        var source = CreateMinimalProfile("Source", 5);
        var destination = Path.Combine(_root, "Occupied");
        Directory.CreateDirectory(destination);
        var sentinel = Path.Combine(destination, "keep-me.txt");
        File.WriteAllText(sentinel, "unchanged");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new QuickMailProfileImporter().ImportAsync(new(source, destination)));

        Assert.Contains("not empty", error.Message);
        Assert.Equal("unchanged", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(destination, "mail.db")));
    }

    private string CreateMinimalProfile(string name, int schemaVersion)
    {
        var profile = Path.Combine(_root, name);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "accounts.json"), "[]");
        using var database = Open(Path.Combine(profile, "mail.db"));
        Execute(database, $"PRAGMA user_version={schemaVersion};");
        Execute(database, "CREATE TABLE Fixture (id INTEGER PRIMARY KEY, value TEXT);");
        Execute(database, "INSERT INTO Fixture(value) VALUES ('source remains intact');");
        return profile;
    }

    private static SqliteConnection Open(string path, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch { }
    }
}
