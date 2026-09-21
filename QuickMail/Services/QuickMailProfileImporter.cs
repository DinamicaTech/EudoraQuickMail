using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QuickMail.Services;

public sealed record QuickMailProfileImportOptions(
    string SourceProfile,
    string DestinationProfile,
    bool StartOffline = true);

public sealed record QuickMailProfileImportResult(
    string DestinationProfile,
    int FilesCopied,
    long BytesCopied,
    int SourceSchemaVersion);

/// <summary>
/// Creates an independent Eudora QuickMail profile from a QuickMail profile. The source is
/// read-only: SQLite is copied through its online-backup API and user-owned companion files are
/// staged before anything is committed to the destination.
/// </summary>
public sealed class QuickMailProfileImporter
{
    // QuickMail upstream still uses user_version 5. A larger value can mean a future, incompatible
    // QuickMail release or an Eudora QuickMail profile and must not be guessed at silently.
    internal const int MaxSupportedQuickMailSchemaVersion = 5;

    private static readonly JsonSerializerOptions ReceiptJsonOptions = new() { WriteIndented = true };

    private static readonly HashSet<string> ProfileFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "accounts.json", "config.ini", "contacts.json", "groups.json", "flags.json",
        "views.json", "rules.json", "templates.json", "hotkeys.json", "folderviews.json",
        "message-list-layout.json", "rowlayout.json", "watches.json", "custom.lex",
        "translation.json", "scheduled-mail.json", "unsubscribe-preferences.json",
    };

    private static readonly HashSet<string> ProfileDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Attachments", "themes",
    };

    private static readonly HashSet<string> IgnorableDestinationFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "quickmail.log", "performance.log", "connection.log",
    };

    public Task<QuickMailProfileImportResult> ImportAsync(
        QuickMailProfileImportOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportCore(options, progress, cancellationToken), cancellationToken);

    internal static bool IsProfileRunning(string profileDirectory)
    {
        var key = SingleInstanceService.ProfileKey(["--profileDir", profileDirectory]);
        try
        {
            using var mutex = Mutex.OpenExisting($@"Local\QuickMail-{key}");
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // If the owner is elevated or belongs to another session, fail closed. Copying a live
            // profile's JSON files independently of its SQLite snapshot would not be trustworthy.
            return true;
        }
    }

    private static QuickMailProfileImportResult ImportCore(
        QuickMailProfileImportOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var source = NormalizeDirectory(options.SourceProfile);
        var destination = NormalizeDirectory(options.DestinationProfile);
        ValidatePaths(source, destination);

        var sourceDatabase = Path.Combine(source, "mail.db");
        var sourceAccounts = Path.Combine(source, "accounts.json");
        if (!File.Exists(sourceDatabase) || !File.Exists(sourceAccounts))
            throw new InvalidDataException(
                "The selected folder is not a QuickMail profile. It must contain mail.db and accounts.json.");
        if (IsProfileRunning(source))
            throw new InvalidOperationException(
                "QuickMail is currently using the selected profile. Close QuickMail completely and try again.");

        EnsureDestinationIsUnused(destination);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The destination profile must have a parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent,
            $".{Path.GetFileName(destination)}-quickmail-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        var committedPaths = new List<string>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Validating the QuickMail database…");
            var sourceSchemaVersion = ReadAndValidateSchema(sourceDatabase);

            progress?.Report("Creating a consistent database snapshot…");
            var stagedDatabase = Path.Combine(staging, "mail.db");
            BackupDatabase(sourceDatabase, stagedDatabase);
            VerifyDatabase(stagedDatabase);

            var filesCopied = 1;
            long bytesCopied = new FileInfo(stagedDatabase).Length;

            progress?.Report("Copying QuickMail settings and user data…");
            foreach (var fileName in ProfileFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var from = Path.Combine(source, fileName);
                if (!File.Exists(from)) continue;
                var to = Path.Combine(staging, fileName);
                File.Copy(from, to, overwrite: false);
                filesCopied++;
                bytesCopied += new FileInfo(to).Length;
            }

            foreach (var directoryName in ProfileDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var from = Path.Combine(source, directoryName);
                if (!Directory.Exists(from)) continue;
                var copied = CopyDirectoryWithoutLinks(from, Path.Combine(staging, directoryName), cancellationToken);
                filesCopied += copied.Files;
                bytesCopied += copied.Bytes;
            }

            if (options.StartOffline)
            {
                using var activity = new MailActivityPolicyService(new ProfileContext(staging));
                activity.SetOffline();
                filesCopied++;
                bytesCopied += new FileInfo(Path.Combine(staging, "mail-activity.json")).Length;
            }

            var receipt = new
            {
                kind = "QuickMail profile import",
                importedUtc = DateTimeOffset.UtcNow,
                sourceSchemaVersion,
                startOffline = options.StartOffline,
            };
            File.WriteAllText(Path.Combine(staging, "quickmail-profile-import.json"),
                JsonSerializer.Serialize(receipt, ReceiptJsonOptions));
            filesCopied++;

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Activating the imported profile…");
            Directory.CreateDirectory(destination);
            foreach (var entry in Directory.EnumerateFileSystemEntries(staging))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(destination, Path.GetFileName(entry));
                if (File.Exists(entry)) File.Move(entry, target);
                else Directory.Move(entry, target);
                committedPaths.Add(target);
            }

            progress?.Report("QuickMail profile import complete.");
            return new QuickMailProfileImportResult(destination, filesCopied, bytesCopied, sourceSchemaVersion);
        }
        catch
        {
            // Roll back only entries this import moved into an otherwise-unused destination.
            foreach (var path in committedPaths.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                }
                catch { /* Preserve the original exception; a receipt is never written on failure. */ }
            }
            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch { /* A stale staging folder is safer than deleting beyond the validated path. */ }
        }
    }

    private static void ValidatePaths(string source, string destination)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"The QuickMail profile does not exist: {source}");
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The source and destination profiles must be different folders.");

        var sourcePrefix = source + Path.DirectorySeparatorChar;
        var destinationPrefix = destination + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The source and destination profiles cannot be located inside one another.");
    }

    private static void EnsureDestinationIsUnused(string destination)
    {
        if (!Directory.Exists(destination)) return;
        var unexpected = Directory.EnumerateFileSystemEntries(destination)
            .Where(path => !File.Exists(path) || !IgnorableDestinationFiles.Contains(Path.GetFileName(path)))
            .Select(Path.GetFileName)
            .ToList();
        if (unexpected.Count > 0)
            throw new InvalidOperationException(
                $"The destination profile is not empty. Choose a new folder. First existing item: {unexpected[0]}");
    }

    private static int ReadAndValidateSchema(string databasePath)
    {
        using var connection = OpenReadOnly(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar() ?? 0);
        if (version > MaxSupportedQuickMailSchemaVersion)
            throw new InvalidDataException(
                $"This profile uses database schema {version}. The importer currently supports QuickMail schemas through {MaxSupportedQuickMailSchemaVersion}.");
        VerifyDatabase(connection);
        return version;
    }

    private static void BackupDatabase(string sourcePath, string destinationPath)
    {
        using var source = OpenReadOnly(sourcePath);
        using var destination = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static void VerifyDatabase(string databasePath)
    {
        using var connection = OpenReadOnly(databasePath);
        VerifyDatabase(connection);
    }

    private static void VerifyDatabase(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(command.ExecuteScalar());
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"QuickMail database integrity check failed: {result}");
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        connection.Open();
        return connection;
    }

    private static (int Files, long Bytes) CopyDirectoryWithoutLinks(
        string source, string destination, CancellationToken cancellationToken)
    {
        var sourceInfo = new DirectoryInfo(source);
        if (sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) return (0, 0);
        Directory.CreateDirectory(destination);
        var files = 0;
        long bytes = 0;
        foreach (var file in sourceInfo.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            file.CopyTo(Path.Combine(destination, file.Name), overwrite: false);
            files++;
            bytes += file.Length;
        }
        foreach (var directory in sourceInfo.EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var copied = CopyDirectoryWithoutLinks(
                directory.FullName, Path.Combine(destination, directory.Name), cancellationToken);
            files += copied.Files;
            bytes += copied.Bytes;
        }
        return (files, bytes);
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
