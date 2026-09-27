using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace DinamicaBackup;

/// <summary>Small, disposable index. Run receipts and manifests are independently readable.</summary>
public sealed class Catalog : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly string _directory;
    private readonly string _localHistory;
    public Catalog(BackupJob job, string? directory = null)
    {
        _directory = directory ?? SafePaths.Child(job.Destination, ".dinamica-backup/" + job.Id);
        SafePaths.NoLinks(_directory);
        Directory.CreateDirectory(_directory);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "backup-catalog.db"), Pooling = false }.ToString());
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS Runs(Id TEXT PRIMARY KEY,Started TEXT,Json TEXT); CREATE TABLE IF NOT EXISTS State(Key TEXT PRIMARY KEY,Value TEXT); CREATE TABLE IF NOT EXISTS Verified(Path TEXT PRIMARY KEY,At TEXT);";
        cmd.ExecuteNonQuery();
        _localHistory = Path.Combine(job.ProfileDirectory, ".backup-history", job.Id);
        var receipts = Directory.EnumerateFiles(_directory, "run-*.json").Concat(Directory.Exists(_localHistory) ? Directory.EnumerateFiles(_localHistory, "run-*.json") : []);
        foreach (var receipt in receipts)
        {
            var run = JsonFiles.Read<RunResult>(receipt);
            InsertRun(run);
        }
        if (Recent(1).Count == 0 && Directory.Exists(job.Destination))
        {
            foreach (var archive in Directory.EnumerateFiles(job.Destination, "backup-*.zip"))
            {
                var manifest = BackupEngine.ReadManifest(archive);
                if (manifest.JobId != job.Id) continue;
                InsertRun(new RunResult { Id = manifest.BackupId, Kind = "Backup", StartedUtc = manifest.CreatedUtc,
                    Status = "Recovered", Artifact = archive, Detail = "Rebuilt from ZIP manifest; run/notification history is unavailable. Run a restore test to reverify." });
            }
        }
    }
    private void InsertRun(RunResult result)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO Runs VALUES($id,$at,$json)";
        cmd.Parameters.AddWithValue("$id", result.Id);
        cmd.Parameters.AddWithValue("$at", result.StartedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(result, JsonFiles.Options));
        cmd.ExecuteNonQuery();
    }
    public void Save(RunResult result)
    {
        JsonFiles.Write(Path.Combine(_localHistory, "run-" + result.Id + ".json"), result);
        JsonFiles.Write(Path.Combine(_directory, "run-" + result.Id + ".json"), result);
        InsertRun(result);
    }
    public List<RunResult> Recent(int limit = 31)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT Json FROM Runs ORDER BY Started DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<RunResult>();
        while (reader.Read()) list.Add(JsonSerializer.Deserialize<RunResult>(reader.GetString(0), JsonFiles.Options)!);
        return list;
    }
    public string? Get(string key)
    {
        using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT Value FROM State WHERE Key=$k";
        cmd.Parameters.AddWithValue("$k", key); return cmd.ExecuteScalar() as string;
    }
    public void Set(string key, string value)
    {
        using var cmd = _db.CreateCommand(); cmd.CommandText = "INSERT OR REPLACE INTO State VALUES($k,$v)";
        cmd.Parameters.AddWithValue("$k", key); cmd.Parameters.AddWithValue("$v", value); cmd.ExecuteNonQuery();
    }
    public Dictionary<string, string> Verifications()
    {
        using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT Path,At FROM Verified";
        using var reader = cmd.ExecuteReader(); var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }
    public void Verified(string path)
    {
        using var cmd = _db.CreateCommand(); cmd.CommandText = "INSERT OR REPLACE INTO Verified VALUES($p,$a)";
        cmd.Parameters.AddWithValue("$p", path); cmd.Parameters.AddWithValue("$a", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }
    public void RecoverInterruptedRuns()
    {
        // Caller owns the exclusive agent lease, so no legitimate runner can still be active.
        foreach (var run in Recent(int.MaxValue).Where(x => x.Status == "Running"))
        {
            run.Status = "Failed"; run.FinishedUtc = DateTimeOffset.UtcNow;
            run.Detail = "Previous agent execution was interrupted before completion."; Save(run);
        }
    }
    public void Dispose() => _db.Dispose();
}
