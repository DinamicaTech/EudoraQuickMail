using System.IO;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Tests;

public sealed class MessageNotesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EQM-notes-" + Guid.NewGuid().ToString("N"));
    private readonly LocalStoreService _store;
    private readonly Guid _account = Guid.NewGuid();
    public MessageNotesTests()
    {
        Directory.CreateDirectory(_root);
        _store = new(new ProfileContext(_root)); _store.Initialize();
    }
    private MailMessageDetail Message(string id = "1", string folder = "In") => new()
    {
        MessageId = id, AccountId = _account, FolderName = folder,
        InternetMessageId = "same@example.com", Subject = "Ordinary subject", Date = DateTimeOffset.UtcNow,
        PlainTextBody = "Ordinary body", Notes = "chocolate proyecto secreto"
    };

    [Theory]
    [InlineData("N:chocolate")]
    [InlineData("chocolate")]
    [InlineData("N:chocola*")]
    [InlineData("N:*ocolat*")]
    [InlineData("N:choco?ate")]
    public async Task NotesAreIndexedInGlobalAndDedicatedSearch(string query)
    {
        await _store.SaveLocalMessageAsync(Message());
        Assert.Equal(1, (await _store.SearchLocalMessagesAsync(new(query))).TotalMatches);
        Assert.Equal("chocolate proyecto secreto", Assert.Single(await _store.LoadFolderSummariesAsync(_account,"In")).Notes);
        Assert.Equal("chocolate proyecto secreto", (await _store.LoadDetailAsync(_account,"In","1"))!.Notes);
    }

    [Fact]
    public async Task GlobalSearchUnionsNotesAndBodyWithoutDuplicatesAndKeepsPagingAndScope()
    {
        var first = Message("1"); first.Notes = "Mitsubishi"; first.PlainTextBody = "Mitsubishi";
        var second = Message("2"); second.InternetMessageId = "two@example.com";
        second.Notes = "Mitsubishi";
        var third = Message("3", "Out"); third.InternetMessageId = "three@example.com";
        third.Notes = ""; third.PlainTextBody = "Mitsubishi";
        await _store.SaveLocalMessageAsync(first, TestContext.Current.CancellationToken);
        await _store.SaveLocalMessageAsync(second, TestContext.Current.CancellationToken);
        await _store.SaveLocalMessageAsync(third, TestContext.Current.CancellationToken);
        var result = await _store.SearchLocalMessagesAsync(new("Mitsubishi", Limit: 1, Offset: 1), TestContext.Current.CancellationToken);
        Assert.Equal(3, result.TotalMatches);
        Assert.Single(result.Messages);
        result = await _store.SearchLocalMessagesAsync(new("\"Mitsubishi?\"", FolderScopes: [new(_account, "In")]), TestContext.Current.CancellationToken);
        Assert.Equal(2, result.TotalMatches);
        Assert.All(result.Messages, message => Assert.Equal("In", message.FolderName));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.SearchLocalMessagesAsync(new("Mitsubishi"), cancelled.Token));
        Assert.Equal(3, (await _store.SearchLocalMessagesAsync(new("Mitsubishi"), TestContext.Current.CancellationToken)).TotalMatches);
    }

    [Fact]
    public void BareNStillMeansUnreadAndNColonMeansNotes()
    {
        Assert.Equal(QuickSearchField.Unread, QuickSearchParser.Parse("N").Groups[0].Alternatives[0].Field);
        Assert.Equal(QuickSearchField.Notes, QuickSearchParser.Parse("N:secret").Groups[0].Alternatives[0].Field);
    }

    [Theory]
    [InlineData(10, 0, 3)]
    [InlineData(2, 0, 2)]
    [InlineData(2, 2, 1)]
    [InlineData(2, 3, 0)]
    [InlineData(2, 20, 0)]
    public async Task IndexedTextAndNotesKeepExactTotalsForFullShortAndEmptyPages(int limit, int offset, int expectedRows)
    {
        for (var i = 1; i <= 3; i++)
        {
            var message = Message(i.ToString());
            message.InternetMessageId = $"{i}@example.com";
            message.Date = DateTimeOffset.UtcNow.AddDays(-i);
            message.Notes = i <= 2 ? "rollinmats" : "";
            message.PlainTextBody = i >= 2 ? "rollinmats" : "ordinary body";
            await _store.SaveLocalMessageAsync(message, TestContext.Current.CancellationToken);
        }

        var result = await _store.SearchLocalMessagesAsync(new("rollinmats", Limit: limit, Offset: offset),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.TotalMatches);
        Assert.Equal(expectedRows, result.Messages.Count);
        Assert.Equal(new[] { "1", "2", "3" }.Skip(offset).Take(limit), result.Messages.Select(m => m.MessageId));
    }

    [Theory]
    [InlineData("rollinmats")]
    [InlineData("rollin*")]
    [InlineData("rollinmats;N")]
    public async Task IndexedTextAndNotesRespectAccountAndExcludedFolderScopes(string query)
    {
        var noteOnly = Message();
        noteOnly.Notes = "rollinmats";
        noteOnly.IsRead = false;
        await _store.SaveLocalMessageAsync(noteOnly, TestContext.Current.CancellationToken);
        var otherAccount = Message();
        otherAccount.AccountId = Guid.NewGuid();
        otherAccount.Notes = "";
        otherAccount.PlainTextBody = "rollinmats";
        await _store.SaveLocalMessageAsync(otherAccount, TestContext.Current.CancellationToken);
        var excluded = Message("1", "Draft");
        excluded.InternetMessageId = "draft@example.com";
        excluded.PlainTextBody = "rollinmats";
        excluded.Notes = "rollinmats";
        await _store.SaveLocalMessageAsync(excluded, TestContext.Current.CancellationToken);

        var result = await _store.SearchLocalMessagesAsync(new(query, AccountIds: [_account],
            ExcludedFolderScopes: [new(_account, "Draft")]), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.TotalMatches);
        var match = Assert.Single(result.Messages);
        Assert.Equal(_account, match.AccountId);
        Assert.Equal("In", match.FolderName);
    }

    [Fact]
    public async Task NotesSurviveLocalMovesAndSortAscendingAndDescending()
    {
        var first=Message(); first.Notes="zebra";
        var second=Message("2","Out"); second.InternetMessageId="second@example.com"; second.Notes="apple";
        await _store.SaveLocalMessageAsync(first); await _store.SaveLocalMessageAsync(second);
        await _store.MoveLocalMessagesAsync(_account,"In","Out",["1"]);
        Assert.Equal("zebra",(await _store.LoadDetailAsync(_account,"Out","1"))!.Notes);
        Assert.Equal("2",(await _store.LoadLocalPageAsync(_account,"Out",20,0,LocalSearchSort.NotesAscending)).Messages[0].MessageId);
        Assert.Equal("1",(await _store.LoadLocalPageAsync(_account,"Out",20,0,LocalSearchSort.NotesDescending)).Messages[0].MessageId);
        Assert.Equal(1,(await _store.SearchLocalMessagesAsync(new("N:zebra"))).TotalMatches);
    }

    [Fact]
    public async Task EditsSurviveRefreshAndNewImapUidAndCanBeCleared()
    {
        await _store.SaveLocalMessageAsync(Message());
        await _store.UpdateStoredMessageContentAsync(_account,"In","1","Ordinary subject","Body",null,ComposeMode.PlainText,"revised annotation");
        var summary = Message(); summary.Notes = "";
        await _store.UpsertSummariesAsync([summary]);
        var copy = Message("2","INBOX"); copy.Notes = "";
        await _store.UpsertSummariesAsync([copy]);
        Assert.All((await _store.LoadAllSummariesAsync()), m => Assert.Equal("revised annotation",m.Notes));
        await _store.UpdateStoredMessageContentAsync(_account,"In","1","Ordinary subject","Body",null,ComposeMode.PlainText,"");
        Assert.All((await _store.LoadAllSummariesAsync()), m => Assert.Equal("",m.Notes));
        Assert.Equal(0,(await _store.SearchLocalMessagesAsync(new("N:revised"))).TotalMatches);
    }

    [Fact]
    public async Task SnoozeAndSpamReadersRetainNotes()
    {
        await _store.EnsureLocalSystemFoldersAsync([new AccountModel
        {
            Id = _account, BackendKind = BackendKind.Pop3Smtp, IsActive = true,
        }]);
        await _store.SaveLocalMessageAsync(Message());
        var wake=DateTimeOffset.UtcNow.AddHours(2);
        await _store.SnoozeMessagesAsync([new(_account,"In","1")],wake);
        Assert.Equal("chocolate proyecto secreto",Assert.Single(await _store.LoadSnoozedMessagesAsync()).Notes);
        Assert.Equal("chocolate proyecto secreto",Assert.Single(await _store.WakeDueSnoozedMessagesAsync(wake)).Notes);
        Assert.Equal("chocolate proyecto secreto",(await _store.LoadExactSpamCandidateAsync(_account,"In","1"))!.Notes);
    }

    [Fact]
    public async Task ComposeIdentityNotesAreRestoredOnSyncAndInitializationIsIdempotent()
    {
        await _store.SavePrivateNotesAsync(_account,"same@example.com","privatekeyword");
        var summary=Message(); summary.Notes="";
        await _store.UpsertSummariesAsync([summary]);
        _store.Initialize();
        Assert.Equal(1,(await _store.SearchLocalMessagesAsync(new("N:privatekeyword"))).TotalMatches);
    }

    [Fact]
    public void PrivateNotesAreNotIncludedInMimeAndComposeRoundTrips()
    {
        var model = new ComposeModel { AccountId=_account, To="you@example.com",Subject="Hello",Body="Body",Notes="privatekeyword" };
        using var vm = new ComposeViewModel(new StubSmtpService(),new StubAccountService(),new StubCredentialService(),new StubImapMailService(),new StubTemplateService());
        vm.Seed(model);
        Assert.Equal(model.Notes,vm.BuildComposeModel(_account).Notes);
        using var mime = MimeMessageBuilder.Build(model,new AccountModel { Username="me@example.com" });
        Assert.DoesNotContain("privatekeyword",mime.ToString());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root,true);
    }
}
