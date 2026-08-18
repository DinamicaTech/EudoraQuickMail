namespace EudoraImporter;

internal static class MigrationCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var source = Value(args, "--source") ?? throw new ArgumentException("Missing --source.");
        var profile = Value(args, "--profile") ?? throw new ArgumentException("Missing --profile.");
        var quickMail = Value(args, "--quickmail");
        var intermediate = Path.Combine(Path.GetFullPath(profile), "eudora-import.db");
        Console.Title = "QuickMail — Import from Eudora";
        Console.WriteLine("QuickMail Eudora migration");
        Console.WriteLine("Progress is shown mailbox by mailbox below.");
        Console.WriteLine("The previous Eudora import will be replaced; other accounts are preserved.");
        Console.WriteLine();
        try
        {
            var imported = await ImportCommand.RunAsync(["--source", source, "--output", intermediate, "--replace"]);
            if (imported != 0) return imported;
            return await NativeProfileImportCommand.RunAsync(["--database", intermediate, "--profile", profile]);
        }
        finally
        {
            if (File.Exists(intermediate)) File.Delete(intermediate);
            if (File.Exists(intermediate + "-wal")) File.Delete(intermediate + "-wal");
            if (File.Exists(intermediate + "-shm")) File.Delete(intermediate + "-shm");
            if (!string.IsNullOrWhiteSpace(quickMail) && File.Exists(quickMail))
            {
                Console.WriteLine();
                Console.WriteLine("Reopening QuickMail…");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(quickMail)
                {
                    UseShellExecute = true,
                });
            }
        }
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
