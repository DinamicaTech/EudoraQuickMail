using System.Diagnostics;
using System.IO;

namespace QuickMail.Services;

public sealed record EudoraImportOptions(
    string EudoraRoot,
    string DataFolder,
    string RootDisplayName,
    string AttachmentMode);

/// <summary>Starts the external Eudora migration with one argument-safe code path.</summary>
public static class EudoraImportLauncher
{
    public static bool TryStart(EudoraImportOptions options, out string? error)
    {
        var importer = Path.Combine(AppContext.BaseDirectory, "EudoraImporter.exe");
        if (!File.Exists(importer))
        {
            error = "EudoraImporter.exe was not found beside QuickMail.exe.";
            return false;
        }

        var target = ProfileContext.TryCreate(options.DataFolder, out error);
        if (target is null) return false;

        try
        {
            var start = new ProcessStartInfo(importer)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var argument in new[]
            {
                "migrate", "--source", options.EudoraRoot, "--profile", target.ProfileDir,
                "--quickmail", Environment.ProcessPath ?? string.Empty,
                "--root-name", options.RootDisplayName, "--attachments", options.AttachmentMode,
            }) start.ArgumentList.Add(argument);
            Process.Start(start);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not start the Eudora importer: {ex.Message}";
            return false;
        }
    }
}
