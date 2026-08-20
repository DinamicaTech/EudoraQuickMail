namespace EudoraImporter;

internal static class MigrationCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var source = Value(args, "--source") ?? throw new ArgumentException("Missing --source.");
        var profile = Value(args, "--profile") ?? throw new ArgumentException("Missing --profile.");
        var quickMail = Value(args, "--quickmail");
        var rootName = Value(args, "--root-name") ?? "Eudora";
        var attachmentMode = (Value(args, "--attachments") ?? "keep").ToLowerInvariant();
        if (attachmentMode is not ("keep" or "copy" or "move"))
            throw new ArgumentException("--attachments must be keep, copy, or move.");
        var intermediate = Path.Combine(Path.GetFullPath(profile), "eudora-import.db");
        Console.Title = "QuickMail — Import from Eudora";
        Console.WriteLine("QuickMail Eudora migration");
        Console.WriteLine("Progress is shown mailbox by mailbox below.");
        Console.WriteLine("The previous Eudora import will be replaced; other accounts are preserved.");
        Console.WriteLine();
        try
        {
            var imported = await ImportCommand.RunAsync(["--source", source, "--output", intermediate, "--replace"]);
            if (imported != 0)
            {
                WriteResult(profile, $"Eudora mailbox import failed with exit code {imported}.");
                return imported;
            }
            var relocation = attachmentMode == "keep"
                ? AttachmentRelocator.Result.Empty
                : await AttachmentRelocator.CopyReferencedAsync(intermediate, source,
                    Path.Combine(Path.GetFullPath(profile), "Attachments", "Eudora"));
            var result = await NativeProfileImportCommand.RunAsync([
                "--database", intermediate, "--profile", profile,
                "--eudora-root", source, "--root-name", rootName]);
            if (result == 0 && attachmentMode == "move")
                AttachmentRelocator.DeleteVerifiedSources(relocation, source);
            WriteResult(profile, result == 0
                ? $"Eudora import completed successfully at {DateTimeOffset.Now:O}."
                : $"Eudora profile import failed with exit code {result}.");
            return result;
        }
        catch (Exception ex)
        {
            WriteResult(profile, $"Eudora import failed at {DateTimeOffset.Now:O}.\n{ex}");
            Console.Error.WriteLine($"Import failed: {ex.Message}");
            return 2;
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
                    Arguments = $"--profileDir \"{Path.GetFullPath(profile)}\"",
                });
            }
        }
    }

    private static void WriteResult(string profile, string message)
    {
        try
        {
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "eudora-import.log"), message);
        }
        catch { }
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
