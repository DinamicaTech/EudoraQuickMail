using System.Diagnostics;
using Microsoft.Win32;

namespace EudoraImporter;

internal static class ImportCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help")) { PrintUsage(); return 0; }
        try
        {
            var sourceArg = ValueAfter(args, "--source");
            var source = sourceArg is null ? DiscoverEudoraDataDirectory() : Path.GetFullPath(sourceArg);
            if (source is null)
                throw new CommandLineException("No se encontró Eudora. Indique su directorio de datos con --source.");
            if (!Directory.Exists(source))
                throw new CommandLineException($"No existe el directorio de origen: {source}");

            var output = Path.GetFullPath(ValueAfter(args, "--output") ?? "eudora-messages.db");
            var replace = args.Any(a => a.Equals("--replace", StringComparison.OrdinalIgnoreCase));
            if (File.Exists(output) && !replace)
                throw new CommandLineException($"Ya existe el archivo de salida: {output}{Environment.NewLine}Use --replace para sustituirlo.");

            var mailboxes = EudoraMailboxDiscovery.Find(source).ToList();
            var mailboxFilter = ValueAfter(args, "--mailbox");
            if (!string.IsNullOrWhiteSpace(mailboxFilter))
                mailboxes = mailboxes.Where(m => m.DisplayName.Equals(mailboxFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mailboxes.Count == 0)
                throw new CommandLineException($"No se encontraron buzones .mbx en {source}");

            var outputDirectory = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(outputDirectory);
            var temporary = Path.Combine(outputDirectory, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
            var stopwatch = Stopwatch.StartNew();
            try
            {
                Console.WriteLine($"Origen:  {source}");
                Console.WriteLine($"Destino: {output}");
                Console.WriteLine($"Buzones: {mailboxes.Count}");
                var result = await EudoraImportService.ImportAsync(source, temporary, mailboxes,
                    progress => Console.WriteLine(
                        $"[{progress.MailboxesCompleted}/{progress.MailboxCount}] {progress.Mailbox}: {progress.MessagesImported:N0} mensajes"));
                File.Move(temporary, output, replace);
                stopwatch.Stop();
                Console.WriteLine($"Importación terminada: {result.MessageCount:N0} mensajes, " +
                                  $"{result.ErrorCount:N0} errores, {stopwatch.Elapsed:c}.");
                if (result.ErrorCount > 0)
                    Console.WriteLine("Los mensajes no analizados se detallan en la tabla import_errors.");
                return 0;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (CommandLineException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; }
    }

    internal static string? DiscoverEudoraDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Qualcomm\Eudora\CommandLine");
            if (key?.GetValue("current") is string commandLine)
            {
                var parts = WindowsCommandLine.Split(commandLine);
                if (parts.Count >= 2 && Directory.Exists(parts[1])) return Path.GetFullPath(parts[1]);
            }
        }
        var candidates = new[]
        {
            @"C:\Eudora",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Qualcomm", "Eudora"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Eudora"),
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string? ValueAfter(string[] args, string option)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(option, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                throw new CommandLineException($"Falta el valor de {option}.");
            return args[i + 1];
        }
        return null;
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        Importador de mensajes de Eudora a SQLite/FTS5

        Uso:
          dotnet run --project Tools/EudoraImporter -- [opciones]

        Opciones:
          --source <directorio>  Directorio de datos de Eudora (se autodetecta si se omite)
          --output <archivo>     Base SQLite de salida (predeterminado: eudora-messages.db)
          --replace              Sustituye una base de salida existente
          --help                 Muestra esta ayuda
        """);

    private sealed class CommandLineException(string message) : Exception(message);
}

internal static class WindowsCommandLine
{
    public static IReadOnlyList<string> Split(string value)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in value)
        {
            if (character == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(character);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
