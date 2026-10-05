using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class PortableUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eqm-update-test-" + Guid.NewGuid().ToString("N"));

    private string Write(string relative, string contents)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    [Theory]
    [InlineData("../Data/mail.db")]
    [InlineData("Assets/../../Data/mail.db")]
    [InlineData("Assets\\..\\secret")]
    [InlineData("/EudoraQM.exe")]
    [InlineData("C:/EudoraQM.exe")]
    [InlineData("Data/mail.db")]
    [InlineData("config.ini")]
    [InlineData("QuickMailDataPath.txt")]
    [InlineData("Assets/file:stream")]
    [InlineData("Assets/folder./file")]
    [InlineData("Assets//file")]
    public void RejectsDataAndUnsafePaths(string path) =>
        Assert.Throws<InvalidDataException>(() => PortableUpdateFiles.Relative(path));

    [Fact]
    public void ApplyPreservesDataConfigAndPreviousVersion()
    {
        Write("stage/EudoraQM.exe", "new");
        Write("stage/Assets/editor.html", "new asset");
        Write("stage/BackupAgent/DinamicaBackupAgent.exe", "new agent");
        Write("target/EudoraQM.exe", "old");
        Write("target/Data/mail.db", "mail");
        Write("target/config.ini", "settings");
        PortableUpdateFiles.Apply(Path.Combine(_root, "stage"), Path.Combine(_root, "target"), Path.Combine(_root, "previous"));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "target/EudoraQM.exe")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "previous/EudoraQM.exe")));
        Assert.Equal("mail", File.ReadAllText(Path.Combine(_root, "target/Data/mail.db")));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(_root, "target/config.ini")));
        Assert.Equal("new agent", File.ReadAllText(Path.Combine(_root, "target/BackupAgent/DinamicaBackupAgent.exe")));
    }

    [Fact]
    public void WriteFailureRollsBackAlreadyReplacedFiles()
    {
        Write("stage/EudoraQM.exe", "new");
        Write("stage/Assets/a.html", "new asset");
        Write("stage/Assets/b.html", "new asset");
        Write("target/EudoraQM.exe", "old");
        var writes = 0;
        Assert.Throws<AggregateException>(() => PortableUpdateFiles.Apply(Path.Combine(_root, "stage"),
            Path.Combine(_root, "target"), Path.Combine(_root, "previous"), _ =>
            { if (++writes == 3) throw new IOException("Simulated write failure"); }));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "target/EudoraQM.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "target/Assets/a.html")));
        Assert.False(File.Exists(Path.Combine(_root, "target/Assets/b.html")));
    }

    [Fact]
    public void ExistingRollbackIsNeverOverwritten()
    {
        Write("stage/EudoraQM.exe", "new"); Write("target/EudoraQM.exe", "old"); Write("previous/EudoraQM.exe", "keep");
        Assert.Throws<IOException>(() => PortableUpdateFiles.Apply(Path.Combine(_root, "stage"), Path.Combine(_root, "target"), Path.Combine(_root, "previous")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "target/EudoraQM.exe")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_root, "previous/EudoraQM.exe")));
    }

    [Fact]
    public void IncompletePackageLeavesInstallationIntact()
    {
        Write("stage/Assets/test.txt", "asset"); Write("target/EudoraQM.exe", "old");
        Assert.Throws<InvalidDataException>(() => PortableUpdateFiles.Apply(Path.Combine(_root, "stage"), Path.Combine(_root, "target"), Path.Combine(_root, "previous")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "target/EudoraQM.exe")));
    }

    [Theory]
    [InlineData("Data/mail.db")]
    [InlineData("Assets/../../mail.db")]
    public void MaliciousArchiveCannotWriteData(string entry)
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "archive.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) writer.Write("bad");
        Assert.Throws<InvalidDataException>(() => PortableUpdateService.Extract(archive, Path.Combine(_root, "stage"), default));
    }

    [Theory]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.Arm64, "arm64")]
    public void ChoosesCompletePackageForProcessArchitecture(Architecture architecture, string rid)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { assets = new[] { Asset("x64"), Asset("arm64") } }));
        var selected = PortableUpdateService.FindRelease(doc.RootElement, "0.8.99", architecture);
        Assert.NotNull(selected);
        Assert.EndsWith($"win-{rid}.zip", selected.Url);
    }

    [Fact]
    public void MissingDigestDoesNotOfferUnverifiedInstall()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { assets = new[] { new
        {
            name = "EudoraQM-v0.8.99-win-x64.zip", size = 10,
            browser_download_url = "https://github.com/DinamicaTech/EudoraQuickMail/releases/download/v0.8.99/EudoraQM-v0.8.99-win-x64.zip"
        } } }));
        Assert.Null(PortableUpdateService.FindRelease(doc.RootElement, "0.8.99", Architecture.X64));
    }

    private static object Asset(string rid) => new
    {
        name = $"EudoraQM-v0.8.99-win-{rid}.zip", size = 10, digest = "sha256:" + new string('a', 64),
        browser_download_url = $"https://github.com/DinamicaTech/EudoraQuickMail/releases/download/v0.8.99/EudoraQM-v0.8.99-win-{rid}.zip"
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
