using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using QuickMail.Services;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) return;
        var work = AppContext.BaseDirectory;
        try
        {
            var requestPath = Path.GetFullPath(args[0]);
            if (!string.Equals(Path.GetDirectoryName(requestPath), Path.TrimEndingDirectorySeparator(work), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid update request location.");
            using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
            var root = request.RootElement;
            var target = root.GetProperty("Target").GetString()!;
            var executable = PortableUpdateFiles.SafePath(target, "EudoraQM.exe");
            using var parent = Process.GetProcessById(root.GetProperty("Pid").GetInt32());
            if (parent.StartTime.ToUniversalTime().Ticks != root.GetProperty("Started").GetInt64()
                || !string.Equals(parent.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The update request does not match the running application.");
            using var installationLock = new FileStream(Path.Combine(target, ".quickmail-update.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            File.WriteAllText(Path.Combine(work, "ready"), "ready");
            // Never kill EQM. Require both a commit marker and a normal process exit.
            var wait = Stopwatch.StartNew();
            while (!parent.WaitForExit(250))
                if (File.Exists(Path.Combine(work, "abort")) || wait.Elapsed > TimeSpan.FromMinutes(2)) return;
            if (File.Exists(Path.Combine(work, "abort")) || !File.Exists(Path.Combine(work, "commit"))) return;
            // Another profile using the same binaries or an active backup agent must not be replaced.
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    string? path;
                    try { path = process.MainModule?.FileName; } catch { continue; }
                    if (path != null && path.StartsWith(Path.TrimEndingDirectorySeparator(target) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Another application is still using this installation. Close it and retry the update.");
                }
            }
            PortableUpdateFiles.Apply(Path.Combine(work, "stage"), target, Path.Combine(work, "previous"));
            File.WriteAllText(Path.Combine(work, "result.log"), "Update installed successfully at " + DateTimeOffset.Now);
            installationLock.Dispose(); // allow the new version to start
            var start = new ProcessStartInfo(executable) { UseShellExecute = true,
                WorkingDirectory = root.GetProperty("WorkingDirectory").GetString()! };
            foreach (var argument in root.GetProperty("Arguments").EnumerateArray()) start.ArgumentList.Add(argument.GetString()!);
            Process.Start(start);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(work, "result.log"), ex.ToString());
            MessageBox.Show("The update could not be completed. Your mail and settings have not been changed.\n\n"
                + ex.Message + "\n\nDetails and previous application files: " + work,
                "Eudora QuickMail Update", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
