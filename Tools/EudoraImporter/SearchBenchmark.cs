using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace EudoraImporter;

internal static class SearchBenchmark
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help")) { PrintUsage(); return 0; }
        try
        {
            var database = Path.GetFullPath(ValueAfter(args, "--database") ?? "eudora-messages.db");
            var query = ValueAfter(args, "--query") ?? throw new ArgumentException("Falta --query.");
            var iterations = ParsePositiveInt(ValueAfter(args, "--iterations") ?? "10", "--iterations");
            var limit = ParsePositiveInt(ValueAfter(args, "--limit") ?? "100", "--limit");
            if (!File.Exists(database)) throw new ArgumentException($"No existe la base: {database}");

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await ExecuteScalarAsync(connection, "PRAGMA query_only=ON;", null);

            var countSql = "SELECT count(*) FROM messages_fts WHERE messages_fts MATCH $query;";
            var (resultCount, coldCount) = await TimeScalarAsync(connection, countSql, query);
            var warmCounts = new List<double>(iterations);
            for (var i = 0; i < iterations; i++)
                warmCounts.Add((await TimeScalarAsync(connection, countSql, query)).ElapsedMs);

            var allIds = await TimeRowsAsync(connection,
                "SELECT rowid FROM messages_fts WHERE messages_fts MATCH $query;", query);
            var allVisibleRows = await TimeRowsAsync(connection, """
                SELECT m.id, m.mailbox, m.date_utc, m.from_addr, m.to_addr, m.subject
                FROM messages_fts f JOIN messages m ON m.id=f.rowid
                WHERE messages_fts MATCH $query;
                """, query);
            var firstPage = await TimeRowsAsync(connection, """
                SELECT m.id, m.mailbox, m.date_utc, m.from_addr, m.to_addr, m.subject
                FROM messages_fts f JOIN messages m ON m.id=f.rowid
                WHERE messages_fts MATCH $query LIMIT $limit;
                """, query, limit);
            var newest = await TimeRowsAsync(connection, """
                SELECT m.id, m.mailbox, m.date_utc, m.from_addr, m.to_addr, m.subject
                FROM messages_fts f JOIN messages m ON m.id=f.rowid
                WHERE messages_fts MATCH $query
                ORDER BY m.date_utc DESC LIMIT $limit;
                """, query, limit);
            var ranked = await TimeRowsAsync(connection, """
                SELECT m.id, m.mailbox, m.date_utc, m.from_addr, m.to_addr, m.subject,
                       bm25(messages_fts) AS rank
                FROM messages_fts f JOIN messages m ON m.id=f.rowid
                WHERE messages_fts MATCH $query
                ORDER BY rank LIMIT $limit;
                """, query, limit);

            warmCounts.Sort();
            Console.WriteLine($"Consulta:                 {query}");
            Console.WriteLine($"Resultados:               {resultCount:N0}");
            Console.WriteLine($"Primer recuento:           {coldCount:F3} ms");
            Console.WriteLine($"Recuento caliente mediano: {Median(warmCounts):F3} ms ({iterations} iteraciones)");
            Console.WriteLine($"Todos los identificadores: {allIds.ElapsedMs:F3} ms ({allIds.RowCount:N0} filas)");
            Console.WriteLine($"Todas las filas visibles:  {allVisibleRows.ElapsedMs:F3} ms ({allVisibleRows.RowCount:N0} filas)");
            Console.WriteLine($"Primera página sin orden:  {firstPage.ElapsedMs:F3} ms ({limit:N0} filas)");
            Console.WriteLine($"Primeras {limit:N0} por fecha:     {newest.ElapsedMs:F3} ms");
            Console.WriteLine($"Primeras {limit:N0} por ranking:   {ranked.ElapsedMs:F3} ms");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or SqliteException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static async Task<(long Value, double ElapsedMs)> TimeScalarAsync(
        SqliteConnection connection, string sql, string query)
    {
        var stopwatch = Stopwatch.StartNew();
        var value = Convert.ToInt64(await ExecuteScalarAsync(connection, sql, query));
        stopwatch.Stop();
        return (value, stopwatch.Elapsed.TotalMilliseconds);
    }

    private static async Task<(long RowCount, double ElapsedMs)> TimeRowsAsync(
        SqliteConnection connection, string sql, string query, int? limit = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$query", query);
        if (limit.HasValue) command.Parameters.AddWithValue("$limit", limit.Value);
        var stopwatch = Stopwatch.StartNew();
        long rows = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows++;
        stopwatch.Stop();
        return (rows, stopwatch.Elapsed.TotalMilliseconds);
    }

    private static async Task<object?> ExecuteScalarAsync(SqliteConnection connection, string sql, string? query)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (query is not null) command.Parameters.AddWithValue("$query", query);
        return await command.ExecuteScalarAsync();
    }

    private static string? ValueAfter(string[] args, string option)
    {
        for (var i = 0; i < args.Length; i++)
            if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"Falta el valor de {option}.");
        return null;
    }

    private static int ParsePositiveInt(string value, string option) =>
        int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"{option} debe ser un entero positivo.");

    private static double Median(IReadOnlyList<double> sorted) => sorted.Count % 2 == 0
        ? (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2
        : sorted[sorted.Count / 2];

    private static void PrintUsage() => Console.WriteLine(
        """
        Benchmark de búsqueda SQLite/FTS5

        Uso:
          dotnet run --project Tools/EudoraImporter -c Release -- benchmark --query <texto> [opciones]

        Opciones:
          --database <archivo>   Base importada (predeterminado: eudora-messages.db)
          --query <texto>        Expresión FTS5 que se medirá
          --iterations <n>       Recuentos calientes (predeterminado: 10)
          --limit <n>            Resultados ordenados por relevancia (predeterminado: 100)
        """);
}
