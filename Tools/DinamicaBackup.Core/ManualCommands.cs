using System.Diagnostics;
using System.Security.Cryptography;

namespace DinamicaBackup;

public sealed record ManualCommand(string Id, string JobId, string Kind, string ConfigurationHash,
    DateTimeOffset ExpiresUtc, bool CancelRequested = false);
public sealed record ManualCommandStatus(string Id, string State, string Detail, long Completed = 0,
    long Total = 0, int WorkerPid = 0, long WorkerStarted = 0, bool ReopenApplication = false, RunResult? Result = null)
{
    public bool Finished => State is "Completed" or "Failed" or "Cancelled" or "Expired";
}

/// <summary>One explicit command per job. Payloads contain no credentials or arbitrary paths/commands.</summary>
public sealed class ManualCommands
{
    private readonly string _directory;
    private readonly string _jobId;
    public ManualCommands(string jobPath, string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid job ID.");
        _jobId = jobId;
        _directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jobPath))!, ".backup-manual", jobId);
    }
    private string RequestPath => Path.Combine(_directory, "request.json");
    private string StatusPath => Path.Combine(_directory, "status.json");
    private FileStream Lock()
    {
        SafePaths.NoLinks(_directory);
        Directory.CreateDirectory(_directory);
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(Path.Combine(_directory, "command.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when (attempt < 30 && (ex.HResult & 0xffff) is 32 or 33) { Thread.Sleep(10); }
        }
    }
    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public ManualCommand Submit(string kind, string hash)
    {
        if (kind is not ("Backup" or "TestDatabaseAccess" or "TestAttachmentAccess")) throw new ArgumentException("Unknown manual operation.");
        using var gate = Lock();
        var previous = ReadStatus();
        if (previous is { Finished: false }) throw new IOException("A manual request is already pending or running. Wait for it or cancel it first.");
        var request = new ManualCommand(Guid.NewGuid().ToString("N"), _jobId, kind, hash, DateTimeOffset.UtcNow.AddMinutes(2));
        JsonFiles.Write(RequestPath, request);
        WriteStatus(new ManualCommandStatus(request.Id, "Pending", "Waiting for the Windows task to accept the request…"));
        return request;
    }
    public ManualCommandStatus? ReadStatus(string? id = null)
    {
        if (id != null && !Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid request ID.");
        var path = id == null ? StatusPath : Path.Combine(_directory, id + ".json");
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return System.Text.Json.JsonSerializer.Deserialize<ManualCommandStatus>(file, JsonFiles.Options);
    }
    private void WriteStatus(ManualCommandStatus status)
    {
        if (!Guid.TryParseExact(status.Id, "N", out _)) throw new InvalidDataException("Invalid request ID.");
        JsonFiles.Write(Path.Combine(_directory, status.Id + ".json"), status);
        JsonFiles.Write(StatusPath, status);
    }
    public ManualCommand? Claim()
    {
        using var gate = Lock();
        if (!File.Exists(RequestPath)) return null;
        var request = JsonFiles.Read<ManualCommand>(RequestPath);
        var state = ReadStatus();
        if (request.JobId != _jobId || state?.Id != request.Id || state.State != "Pending") return null;
        if (request.CancelRequested || request.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            WriteStatus(state with { State = request.CancelRequested ? "Cancelled" : "Expired", Detail = "Request cancelled or expired before execution." });
            return null;
        }
        using var process = Process.GetCurrentProcess();
        WriteStatus(state with { State = "Running", Detail = "Request accepted by Windows task.",
            WorkerPid = process.Id, WorkerStarted = process.StartTime.ToUniversalTime().Ticks });
        return request;
    }
    public void Cancel(string id)
    {
        using var gate = Lock();
        if (!File.Exists(RequestPath)) return;
        var request = JsonFiles.Read<ManualCommand>(RequestPath);
        var status = ReadStatus();
        if (request.Id != id || status?.Id != id || status.Finished) return;
        JsonFiles.Write(RequestPath, request with { CancelRequested = true });
        if (status.State == "Pending") WriteStatus(status with { State = "Cancelled", Detail = "Cancelled before execution." });
    }
    public bool CancellationRequested(string id)
    {
        var request = JsonFiles.Read<ManualCommand>(RequestPath);
        return request.Id != id || request.CancelRequested;
    }
    public void Update(string id, string state, string detail, long completed = 0, long total = 0,
        bool reopen = false, RunResult? result = null)
    {
        using var gate = Lock();
        var current = ReadStatus();
        if (current?.Id != id || current.Finished) return;
        WriteStatus(current with { State = state, Detail = detail, Completed = completed,
            Total = total, ReopenApplication = reopen, Result = result });
    }
    public void RecoverAbandoned()
    {
        using var gate = Lock();
        var status = ReadStatus();
        if (status is { State: "Pending" } && File.Exists(RequestPath)
            && JsonFiles.Read<ManualCommand>(RequestPath).ExpiresUtc <= DateTimeOffset.UtcNow)
            WriteStatus(status with { State = "Expired", Detail = "The task did not accept the request in time. Check registration and update the installed agent." });
        if (status is not { State: "Running" }) return;
        try
        {
            using var process = Process.GetProcessById(status.WorkerPid);
            if (process.StartTime.ToUniversalTime().Ticks == status.WorkerStarted && !process.HasExited) return;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { return; } // unknown is not proof of death
        WriteStatus(status with { State = "Failed", Detail = "The worker stopped unexpectedly. The request will not be repeated automatically; inspect history before retrying." });
    }
}
