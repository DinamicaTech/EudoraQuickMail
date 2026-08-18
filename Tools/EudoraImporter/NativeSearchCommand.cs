using QuickMail.Models;
using QuickMail.Services;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace EudoraImporter;

internal static class NativeSearchCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var profile = Value(args, "--profile") ?? throw new ArgumentException("Falta --profile.");
            var query = Value(args, "--query") ?? throw new ArgumentException("Falta --query.");
            var store = new LocalStoreService(new ProfileContext(Path.GetFullPath(profile)));
            var advancedField = Value(args, "--advanced-field");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = advancedField is null
                ? await store.SearchLocalMessagesAsync(new LocalSearchQuery(query))
                : await store.SearchLocalMessagesAdvancedAsync(new AdvancedSearchQuery(
                    [new AdvancedSearchCriterion(advancedField, "contains", query)]));
            stopwatch.Stop();
            Console.WriteLine($"Consulta: {query}");
            Console.WriteLine($"Coincidencias: {result.TotalMatches:N0}");
            Console.WriteLine($"Filas devueltas: {result.Messages.Count:N0}");
            Console.WriteLine($"Tiempo: {stopwatch.Elapsed.TotalMilliseconds:N1} ms");
            if (args.Any(a => a.Equals("--diagnostics", StringComparison.OrdinalIgnoreCase)))
                await PrintDiagnosticsAsync(Path.Combine(Path.GetFullPath(profile), "mail.db"));
            var importedDatabase = Value(args, "--database");
            if (!string.IsNullOrWhiteSpace(importedDatabase))
                await PrintImportDiagnosticsAsync(Path.GetFullPath(importedDatabase));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"No se pudo validar el índice: {ex.Message}");
            return 2;
        }
    }

    private static async Task PrintImportDiagnosticsAsync(string database)
    {
        await using var conn = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False;");
        await conn.OpenAsync();
        await using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT mailbox,count(*),min(date_utc),max(date_utc) FROM messages
            WHERE mailbox='In' OR mailbox LIKE 'In/%' GROUP BY mailbox ORDER BY mailbox;
            SELECT source_mailbox,source_ordinal,error FROM import_errors
            WHERE source_mailbox='In.mbx' OR source_mailbox LIKE 'In/%';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            Console.WriteLine($"Imported [{reader.GetString(0)}]: {reader.GetInt64(1):N0}; {reader.GetValue(2)} – {reader.GetValue(3)}");
        if (await reader.NextResultAsync())
            while (await reader.ReadAsync())
                Console.WriteLine($"Import error [{reader.GetString(0)} #{reader.GetInt64(1)}]: {reader.GetString(2)}");
        await reader.DisposeAsync();
        await using var bodies = conn.CreateCommand();
        bodies.CommandText = "SELECT count(*),sum(length(body_text)>0),sum(length(body_html)>0),sum(length(body_text)=0 AND length(body_html)=0) FROM messages WHERE mailbox='In';";
        await using var bodyReader = await bodies.ExecuteReaderAsync();
        await bodyReader.ReadAsync();
        Console.WriteLine($"Imported In bodies: {bodyReader.GetInt64(0):N0}; plain {bodyReader.GetInt64(1):N0}; HTML {bodyReader.GetInt64(2):N0}; empty {bodyReader.GetInt64(3):N0}");
    }

    private static async Task PrintDiagnosticsAsync(string database)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        await conn.OpenAsync();
        await using var counts = conn.CreateCommand();
        counts.CommandText = """
            SELECT count(*),
                   sum(CASE WHEN length(plain_body)>0 THEN 1 ELSE 0 END),
                   sum(CASE WHEN length(html_body)>0 THEN 1 ELSE 0 END),
                   sum(CASE WHEN attachments_json IS NOT NULL THEN 1 ELSE 0 END)
            FROM MessageDetail WHERE account_id='ed0da001-0000-4000-8000-000000000001';
            """;
        await using (var reader = await counts.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            Console.WriteLine($"Detalles/cuerpos: {reader.GetInt64(0):N0} / {reader.GetInt64(1):N0}");
            Console.WriteLine($"Mensajes con HTML: {reader.GetInt64(2):N0}");
            Console.WriteLine($"Mensajes con adjuntos enlazados: {reader.GetInt64(3):N0}");
        }
        await using var dates = conn.CreateCommand();
        dates.CommandText = """
            SELECT count(*), sum(CASE WHEN date_ticks=0 THEN 1 ELSE 0 END), min(date_ticks), max(date_ticks)
            FROM MessageSummary WHERE account_id='ed0da001-0000-4000-8000-000000000001' AND folder_name='Out';
            """;
        await using (var reader = await dates.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            Console.WriteLine($"Out: {reader.GetInt64(0):N0} mensajes; fechas vacías: {reader.GetInt64(1):N0}");
            if (!reader.IsDBNull(2))
                Console.WriteLine($"Rango Out: {new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero):d} – {new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero):d}");
        }
        await using var inbox = conn.CreateCommand();
        inbox.CommandText = """
            SELECT folder_name, count(*), min(date_ticks), max(date_ticks)
            FROM MessageSummary
            WHERE account_id='ed0da001-0000-4000-8000-000000000001'
              AND (folder_name='In' OR folder_name LIKE 'In/%')
            GROUP BY folder_name ORDER BY folder_name;
            """;
        await using (var reader = await inbox.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                Console.WriteLine($"In scope [{reader.GetString(0)}]: {reader.GetInt64(1):N0}; " +
                    $"{new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero):d} – {new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero):d}");
        }
        await using var inboxBodies = conn.CreateCommand();
        inboxBodies.CommandText = """
            SELECT count(*),
                   sum(CASE WHEN length(d.plain_body)>0 THEN 1 ELSE 0 END),
                   sum(CASE WHEN length(d.html_body)>0 THEN 1 ELSE 0 END),
                   sum(CASE WHEN length(d.plain_body)=0 AND length(d.html_body)=0 THEN 1 ELSE 0 END)
            FROM MessageDetail d WHERE d.account_id='ed0da001-0000-4000-8000-000000000001' AND d.folder_name='In';
            """;
        await using (var reader = await inboxBodies.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            Console.WriteLine($"In bodies: {reader.GetInt64(0):N0}; plain {reader.GetInt64(1):N0}; HTML {reader.GetInt64(2):N0}; empty {reader.GetInt64(3):N0}");
        }
        long links = 0, existing = 0;
        await using var attachments = conn.CreateCommand();
        attachments.CommandText = "SELECT attachments_json FROM MessageDetail WHERE account_id='ed0da001-0000-4000-8000-000000000001' AND attachments_json IS NOT NULL;";
        await using var attachmentReader = await attachments.ExecuteReaderAsync();
        while (await attachmentReader.ReadAsync())
        {
            using var json = JsonDocument.Parse(attachmentReader.GetString(0));
            foreach (var item in json.RootElement.EnumerateArray())
            {
                links++;
                if (item.TryGetProperty("PartSpecifier", out var path) && File.Exists(path.GetString())) existing++;
            }
        }
        Console.WriteLine($"Referencias de adjuntos existentes: {existing:N0} de {links:N0}");
    }

    private static string? Value(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
