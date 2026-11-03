using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Runs an explicitly configured local program, never a command received in email.</summary>
internal static class RuleExecuteService
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    internal static ProcessStartInfo CreateStartInfo(string? commandLine, bool addSubject, string subject)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Execute requires a single command line with an executable and optional arguments.");
        var memory = CommandLineToArgvW(commandLine.Trim(), out var count);
        if (memory == IntPtr.Zero) throw new ArgumentException("Cannot parse the Execute command line.");
        string[] args;
        try
        {
            args = Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size)) ?? "").ToArray();
        }
        finally { LocalFree(memory); }
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]) ||
            !Path.GetExtension(args[0]).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Specify an .exe program. For PowerShell scripts use powershell.exe -NoProfile -File \"C:\\Scripts\\script.ps1\".");
        var program = Path.GetFileName(args[0]);
        if (addSubject)
        {
            if (subject.Contains('\0')) throw new ArgumentException("The message subject contains an invalid null character.");
            if (program.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Passing an email subject to cmd.exe is unsafe. Use a program or a PowerShell -File script instead.");
            if ((program.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) || program.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)) &&
                !args.Skip(1).TakeWhile(a => !a.Equals("-File", StringComparison.OrdinalIgnoreCase)).All(a => !a.StartsWith("-c", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("-e", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Use PowerShell -File, not -Command or -EncodedCommand, when adding an email subject.");
            if ((program.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) || program.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)) &&
                !args.Any(a => a.Equals("-File", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Use PowerShell -File when adding an email subject.");
        }
        var start = new ProcessStartInfo(args[0]) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in args.Skip(1)) start.ArgumentList.Add(argument);
        if (addSubject) start.ArgumentList.Add(subject);
        return start;
    }

    internal static async Task ExecuteAsync(MailRule rule, MailMessageSummary message, CancellationToken ct)
    {
        var start = CreateStartInfo(rule.CommandLine, rule.AddSubjectToCommandLine, message.Subject ?? "");
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Execute program.");
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("Execute exceeded its 60-second limit; the process was stopped. Its effects cannot be undone.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Execute failed with exit code {process.ExitCode}. Its effects cannot be undone.");
    }
}
