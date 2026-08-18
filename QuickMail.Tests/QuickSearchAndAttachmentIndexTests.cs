using System.IO.Compression;
using System.IO;
using System.Text;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Views;

namespace QuickMail.Tests;

public class QuickSearchParserTests
{
    [Fact]
    public void CombinesOrdinaryTextWithUnreadAndDateCriteria()
    {
        var parsed = QuickSearchParser.Parse("chocolate;N;D>2022");
        Assert.Equal(QuickSearchField.Any, parsed.Groups[0].Alternatives[0].Field);
        Assert.Equal(QuickSearchField.Unread, parsed.Groups[1].Alternatives[0].Field);
        Assert.Equal(QuickSearchField.Date, parsed.Groups[2].Alternatives[0].Field);
    }

    [Fact]
    public void ParsesAndOrPrefixExample()
    {
        var parsed = QuickSearchParser.Parse("T:(@dinamica;@google);A#=2;D=3/5/2026");
        Assert.Equal(3, parsed.Groups.Count);
        Assert.Equal(2, parsed.Groups[0].Alternatives.Count);
        Assert.All(parsed.Groups[0].Alternatives, x => Assert.Equal(QuickSearchField.To, x.Field));
        Assert.Equal(QuickSearchField.AttachmentCount, parsed.Groups[1].Alternatives[0].Field);
        Assert.Equal(QuickSearchField.Date, parsed.Groups[2].Alternatives[0].Field);
    }

    [Fact]
    public void QuotedSemicolonIsPlainText()
    {
        var parsed = QuickSearchParser.Parse("\"B:not a command;still text\"");
        var term = Assert.Single(Assert.Single(parsed.Groups).Alternatives);
        Assert.Equal(QuickSearchField.Any, term.Field);
        Assert.Equal("B:not a command;still text", term.Value);
    }

    [Theory]
    [InlineData("D:2025")]
    [InlineData("S=hello")]
    [InlineData("A#:2")]
    public void RejectsWrongOperatorForField(string query) => Assert.Throws<FormatException>(() => QuickSearchParser.Parse(query));

    [Fact]
    public void DateYearAndMonthProduceRanges()
    {
        var year = QuickSearchParser.ParseDateRange("2015");
        Assert.Equal(2015, year.Start.ToLocalTime().Year);
        Assert.Equal(2016, year.End.ToLocalTime().Year);
        var month = QuickSearchParser.ParseDateRange("03/2015");
        Assert.Equal(3, month.Start.ToLocalTime().Month);
        Assert.Equal(4, month.End.ToLocalTime().Month);
    }
}

public class AttachmentExtractorTests
{
    [Fact]
    public async Task ExtractsTextFromZipWithoutWritingExpandedFile()
    {
        await using var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("folder/note.txt");
            await using var output = entry.Open();
            await using var writer = new StreamWriter(output, Encoding.UTF8, 1024, leaveOpen: true);
            await writer.WriteAsync("Chocolate contract inside archive");
        }
        zipBytes.Position = 0;
        var result = await AttachmentIndexingService.ExtractAsync(zipBytes, "mail.zip", 20 * 1024 * 1024, true, 0, default);
        var item = Assert.Single(result);
        Assert.Equal("folder/note.txt", item.EntryPath);
        Assert.Contains("Chocolate contract", item.Text);
    }

    [Fact]
    public async Task PersistsHashAndReusesExtractionAcrossMessageIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "QuickMail-hash-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new LocalStoreService(new ProfileContext(directory)); store.Initialize();
            var path = Path.Combine(directory, "shared.txt");
            await File.WriteAllTextAsync(path, "shared searchable content", TestContext.Current.CancellationToken);
            var info = new FileInfo(path); const string hash = "ABC123"; const string key = "1:.txt:True:20";
            await store.SaveAttachmentHashAsync(path, info.Length, info.LastWriteTimeUtc.Ticks, hash, TestContext.Current.CancellationToken);
            await store.SaveCachedExtractionAsync(hash, key, [new("", "indexed", "shared searchable content")], TestContext.Current.CancellationToken);

            Assert.Equal(hash, await store.GetCachedAttachmentHashAsync(path, info.Length, info.LastWriteTimeUtc.Ticks, TestContext.Current.CancellationToken));
            var cached = Assert.Single((await store.LoadCachedExtractionAsync(hash, key, TestContext.Current.CancellationToken))!);
            Assert.Equal("shared searchable content", cached.Text);
            await store.ResetAttachmentSearchIndexAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(await store.LoadCachedExtractionAsync(hash, key, TestContext.Current.CancellationToken));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}

public class QuickSearchStoreTests
{
    [Fact]
    public async Task SearchesStructuredFieldsAndAttachmentIndex()
    {
        var directory = Path.Combine(Path.GetTempPath(), "QuickMail-search-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new LocalStoreService(new ProfileContext(directory)); store.Initialize();
            var account = Guid.NewGuid();
            var message = new MailMessageDetail
            {
                AccountId = account, FolderName = "Inbox", MessageId = "m1",
                From = "sender@example.com", To = "ronald@dinamica.tech", Cc = "team@example.com",
                Subject = "Annual report", Date = new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero),
                PlainTextBody = "This body contains choxolate for wildcard testing.",
            };
            await store.SaveLocalMessageAsync(message, TestContext.Current.CancellationToken);
            message.Attachments = [new AttachmentModel { FileName = "report.pdf", PartSpecifier = Path.Combine(directory, "report.pdf") }];
            await store.UpsertDetailAsync(message);
            var candidate = new AttachmentIndexCandidate(account, "m1", "Inbox", "report.pdf", message.Attachments[0].PartSpecifier!, 12);
            await store.ReplaceAttachmentIndexAsync(candidate, "test", [new("inside/report.txt", "indexed", "Chocolate contract")], TestContext.Current.CancellationToken);

            Assert.Equal(1, (await store.SearchLocalMessagesAsync(new("T:@dinamica;A#=1;D=2026"), TestContext.Current.CancellationToken)).TotalMatches);
            Assert.Equal(1, (await store.SearchLocalMessagesAsync(new("B:cho?olate"), TestContext.Current.CancellationToken)).TotalMatches);
            Assert.Equal(1, (await store.SearchLocalMessagesAsync(new("AN:report"), TestContext.Current.CancellationToken)).TotalMatches);
            Assert.Equal(1, (await store.SearchLocalMessagesAsync(new("AC:Chocolate"), TestContext.Current.CancellationToken)).TotalMatches);
            Assert.Equal(1, await store.RebuildMessageSearchIndexAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, (await store.SearchLocalMessagesAsync(new("S:Annual"), TestContext.Current.CancellationToken)).TotalMatches);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}

public class AttachmentIndexSettingsTests
{
    [Fact]
    public void LimitsRoundTripThroughConfigIni()
    {
        var directory = Path.Combine(Path.GetTempPath(), "QuickMail-config-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new ProfileContext(directory); var service = new ConfigService(profile);
            var config = service.Load(); config.IndexAttachmentContents = false; config.IndexCompressedAttachments = false;
            config.AttachmentIndexMaxFileMb = 7; config.AttachmentIndexMaxExpandedMb = 13; service.Save(config);
            var loaded = new ConfigService(profile).Load();
            Assert.False(loaded.IndexAttachmentContents); Assert.False(loaded.IndexCompressedAttachments);
            Assert.Equal(7, loaded.AttachmentIndexMaxFileMb); Assert.Equal(13, loaded.AttachmentIndexMaxExpandedMb);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

public class ComposeLanguageDetectionTests
{
    [Fact]
    public void DetectsSpanishFromFiveWords() =>
        Assert.Equal("es-ES", ComposeWindow.DetectComposeLanguage("hola gracias por este mensaje".Split(' ')));

    [Fact]
    public void DetectsEnglishFromOrdinaryOpening() =>
        Assert.Equal("en-US", ComposeWindow.DetectComposeLanguage("This is a long email".Split(' ')));

    [Fact]
    public void DetectsCatalanAgainstSpanishSharedWords()
    {
        var language = ComposeWindow.DetectComposeLanguage(
            "hola gràcies per aquest missatge que hem escrit amb les dades i els documents per aquesta reunió"
                .Split(' '));
        Assert.Equal("ca-ES", language);
    }

    [Fact]
    public void DetectsCatalanFromShortOrdinaryPhrase() =>
        Assert.Equal("ca-ES", ComposeWindow.DetectComposeLanguage("Anem a fer una prova".Split(' ')));

    [Fact]
    public void AmbiguousTextDoesNotForceLanguage() =>
        Assert.Null(ComposeWindow.DetectComposeLanguage("project budget Ronald Monday August".Split(' ')));
}
