using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Diagnostics;

namespace DinamicaBackup;

public static class SqliteValidation
{
    private static SqliteConnection Open(string path, bool readOnly)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 15 }.ToString());
        c.Open(); return c;
    }
    public static void Checkpoint(string path, IProgress<ProgressInfo>? progress = null)
    {
        using var status = new StageProgress(progress, "SQLite: opening database for WAL checkpoint");
        using var c = Open(path, false);
        status.Set("SQLite: consolidating pending WAL changes into the database (checkpoint)");
        using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var r = cmd.ExecuteReader();
        if (r.Read() && r.GetInt32(0) != 0) throw new IOException("Database checkpoint is busy. Another program may be using it.");
    }
    public static (long Version, Dictionary<string,string?> Probes) Inspect(string path, IEnumerable<SqlProbe> probes, bool full, CancellationToken ct, IProgress<ProgressInfo>? progress = null)
    {
        using var status = new StageProgress(progress, "SQLite: opening database for validation");
        using var c = Open(path, true);
        var elapsed = Stopwatch.StartNew();
        var limit = TimeSpan.FromMinutes(full ? 60 : 30);
        SQLitePCL.raw.sqlite3_progress_handler(c.Handle, 1000,
            _ => ct.IsCancellationRequested || elapsed.Elapsed > limit ? 1 : 0, null);
        using var interrupt = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(c.Handle));
        using var setup = c.CreateCommand(); setup.CommandText = "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;"; setup.ExecuteNonQuery();
        using var cmd = c.CreateCommand();
        using var registration = ct.Register(() => { try { cmd.Cancel(); } catch (ObjectDisposedException) { } });
        cmd.CommandTimeout = 300;
        cmd.CommandText = full ? "PRAGMA integrity_check" : "PRAGMA quick_check";
        status.Set(full ? "SQLite: full integrity check — scanning database; no percentage available" : "SQLite: quick_check — scanning database; large databases may take several minutes");
        if (!string.Equals(cmd.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SQLite integrity check failed.");
        ct.ThrowIfCancellationRequested();
        cmd.CommandText = "PRAGMA foreign_key_check";
        status.Set("SQLite: checking foreign-key relationships");
        using (var r = cmd.ExecuteReader()) if (r.Read()) throw new InvalidDataException("SQLite foreign key check failed.");
        status.Set("SQLite: reading schema version");
        cmd.CommandText = "PRAGMA user_version"; var version = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        var values = new Dictionary<string,string?>();
        foreach (var probe in probes)
        {
            status.Set("SQLite: validation query — " + probe.Name);
            ct.ThrowIfCancellationRequested();
            elapsed.Restart(); limit = TimeSpan.FromSeconds(30);
            var sql = probe.Sql.Trim();
            // Deliberately restricted advanced interface: one SELECT, without comments/statements.
            if (!sql.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase) || sql.Contains(';') || sql.Contains("--") || sql.Contains("/*") || sql.Contains("load_extension", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Validation SQL must be a single SELECT without comments or extensions.");
            cmd.CommandTimeout = 30; cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            if (reader.FieldCount != 1 || !reader.Read()) throw new InvalidDataException("Validation query must return exactly one value: " + probe.Name);
            var value = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (reader.Read()) throw new InvalidDataException("Validation query returned multiple rows: " + probe.Name);
            values.Add(probe.Name, value);
        }
        return (version, values);
    }
}
