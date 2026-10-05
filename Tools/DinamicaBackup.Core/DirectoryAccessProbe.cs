using System.Security.Cryptography;

namespace DinamicaBackup;

public static class DirectoryAccessProbe
{
    public static void Test(string directory, CancellationToken ct)
    {
        SafePaths.NoLinks(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The destination folder does not exist or is not accessible: " + directory);
        ct.ThrowIfCancellationRequested();
        _ = Directory.EnumerateFileSystemEntries(directory).Take(1).ToArray();
        var path = Path.Combine(directory, ".dinamica-access-test-" + Guid.NewGuid().ToString("N") + ".tmp");
        var data = RandomNumberGenerator.GetBytes(128);
        var created = false;
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; file.Write(data); file.Flush(true); }
            ct.ThrowIfCancellationRequested();
            if (!File.ReadAllBytes(path).SequenceEqual(data)) throw new IOException("Read-back verification failed: " + path);
        }
        finally { if (created) File.Delete(path); }
        if (File.Exists(path)) throw new IOException("The probe file could not be deleted: " + path);
    }
}
