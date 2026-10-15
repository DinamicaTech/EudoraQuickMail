using System.IO;
using Microsoft.Data.Sqlite;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class MessageActivityAndBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EQM-activity-" + Guid.NewGuid().ToString("N"));
    private readonly LocalStoreService _store;
    public MessageActivityAndBundleTests()
    {
        Directory.CreateDirectory(_root);
        _store = new(new ProfileContext(_root));
        _store.Initialize();
    }

    [Fact]
    public async Task RefreshDoesNotRepeatArrivalAndActivitySurvivesMove()
    {
        var message = new MailMessageSummary { AccountId = Guid.NewGuid(), MessageId = "1", FolderName = "In", InternetMessageId = "a@example.com" };
        await _store.UpsertSummariesAsync([message]);
        await _store.UpsertSummariesAsync([message]);
        Assert.Single(await _store.LoadMessageActivityAsync(message));
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "mail.db")}");
        await connection.OpenAsync();
        using var move = connection.CreateCommand();
        move.CommandText = "UPDATE MessageSummary SET folder_name='Archive' WHERE unique_id='1';";
        await move.ExecuteNonQueryAsync();
        message.FolderName = "Archive";
        var activity = await _store.LoadMessageActivityAsync(message);
        Assert.Equal(2, activity.Count);
        Assert.Equal("Moved", activity[0].Action);
        Assert.Contains("In → Archive", activity[0].Description);
    }

    [Fact]
    public async Task SchemaIsAddedToExistingCurrentVersionAndScopesAccounts()
    {
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "mail.db")}"))
        {
            await connection.OpenAsync();
            using var reset = connection.CreateCommand();
            reset.CommandText = "DROP TRIGGER qm_activity_insert; DROP TRIGGER qm_activity_move; DROP TRIGGER qm_activity_delete; DROP TRIGGER qm_activity_bound; DROP TABLE MessageActivity; PRAGMA user_version=9;";
            await reset.ExecuteNonQueryAsync();
        }
        _store.Initialize();
        var message = new MailMessageSummary { AccountId = Guid.NewGuid(), MessageId = "1", FolderName = "In", InternetMessageId = "same@example.com" };
        await _store.RecordMessageActivityAsync(message, "Rule", "Moved by invoice rule");
        Assert.Single(await _store.LoadMessageActivityAsync(message));
        message.AccountId = Guid.NewGuid();
        Assert.Empty(await _store.LoadMessageActivityAsync(message));
    }

    [Fact]
    public async Task ActivityResolvesEnrichedInternetIdWithoutCrossingFolders()
    {
        var account = Guid.NewGuid();
        await _store.UpsertSummariesAsync([
            new() { AccountId = account, MessageId = "1", FolderName = "In", InternetMessageId = "in@example.com" },
            new() { AccountId = account, MessageId = "1", FolderName = "Other", InternetMessageId = "other@example.com" }
        ]);
        var stale = new MailMessageSummary { AccountId = account, MessageId = "1", FolderName = "In" };
        await _store.RecordMessageActivityAsync(stale, "Rule", "Expected rule");
        Assert.Contains(await _store.LoadMessageActivityAsync(stale), row => row.Description == "Expected rule");
        stale.FolderName = "Other";
        Assert.DoesNotContain(await _store.LoadMessageActivityAsync(stale), row => row.Description == "Expected rule");
    }

    [Fact]
    public async Task RetentionBoundaryDoesNotKeepAnExtraEvent()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "mail.db")}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO MessageActivity(id,account_id,message_key,action,description) VALUES(1,'a','key','test','first'),(2,'a','key','test','second'),(100001,'a','key','test','last'); SELECT COUNT(*) FROM MessageActivity WHERE id=1;";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task MissingInternetIdHistoryFollowsFolderMove()
    {
        var message = new MailMessageSummary { AccountId = Guid.NewGuid(), MessageId = "99", FolderName = "In" };
        await _store.UpsertSummariesAsync([message]);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "mail.db")}");
        await connection.OpenAsync();
        using var move = connection.CreateCommand();
        move.CommandText = "UPDATE MessageSummary SET folder_name='Saved' WHERE unique_id='99';";
        await move.ExecuteNonQueryAsync();
        message.FolderName = "Saved";
        Assert.Equal(2, (await _store.LoadMessageActivityAsync(message)).Count);
    }

    [Fact]
    public async Task BundleIncludesEmlNativeAttachmentsAndEscapedIndexWithoutNotes()
    {
        var detail = new MailMessageDetail { AccountId = Guid.NewGuid(), MessageId = "1", FolderName = "In",
            From = "sender@example.com", To = "recipient@example.com", Subject = "<script>alert(1)</script>",
            Notes = "PRIVATE-NOTE", PlainTextBody = "hello", Date = DateTimeOffset.UtcNow,
            Attachments = [new AttachmentModel { FileName = "../../report.pdf", Content = [1, 2, 3], ContentType = "application/pdf" }] };
        var store = new StubLocalStoreService { SeededDetail = detail };
        var result = await new ConversationExportService(store, new StubImapMailService()).ExportAsync([detail], _root);
        Assert.Equal(1, result.Exported); Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(Path.Combine(result.Directory, "00001", "message.eml")));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(result.Directory, "00001")).Length);
        var index = await File.ReadAllTextAsync(Path.Combine(result.Directory, "index.html"));
        Assert.DoesNotContain("<script>", index);
        Assert.Contains("&lt;script&gt;", index);
        Assert.DoesNotContain("PRIVATE-NOTE", index);
        var eml = await File.ReadAllTextAsync(Path.Combine(result.Directory, "00001", "message.eml"));
        Assert.DoesNotContain("PRIVATE-NOTE", eml);
    }

    [Fact]
    public async Task BundleMarksMissingAttachmentAsIncomplete()
    {
        var detail = new MailMessageDetail { AccountId = Guid.NewGuid(), MessageId = "1", FolderName = "In", Subject = "Missing",
            Attachments = [new AttachmentModel { FileName = "lost.pdf" }] };
        var result = await new ConversationExportService(new StubLocalStoreService { SeededDetail = detail }, new StubImapMailService()).ExportAsync([detail], _root);
        Assert.Equal(0, result.Exported); Assert.Equal(1, result.Failed);
        Assert.Contains("lost.pdf", await File.ReadAllTextAsync(Path.Combine(result.Directory, "index.html")));
    }

    [Fact]
    public async Task BundleFallsBackToMailServiceWhenLocalStoreThrows()
    {
        var detail = new MailMessageDetail { AccountId = Guid.NewGuid(), FolderName = "INBOX", MessageId = "1", Subject = "Online", PlainTextBody = "Fetched remotely" };
        var mail = new RemoteDetailMail(detail);
        var result = await new ConversationExportService(new StubLocalStoreService { LoadDetailFailure = new IOException("Offline database") }, mail)
            .ExportAsync([detail], _root);
        Assert.Equal(1, result.Exported); Assert.Equal(1, mail.Fetches);
        Assert.Contains("Fetched remotely", await File.ReadAllTextAsync(Path.Combine(result.Directory, "00001", "message.eml")));
    }

    private sealed class RemoteDetailMail(MailMessageDetail detail) : StubImapMailServiceBase
    {
        public int Fetches { get; private set; }
        public override Task<MailMessageDetail> PrefetchMessageDetailAsync(Guid accountId, string folderName, string messageId, CancellationToken ct = default)
        { Fetches++; return Task.FromResult(detail); }
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
