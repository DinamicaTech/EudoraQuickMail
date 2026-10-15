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
    public void BareNStillMeansUnreadAndNColonMeansNotes()
    {
        Assert.Equal(QuickSearchField.Unread, QuickSearchParser.Parse("N").Groups[0].Alternatives[0].Field);
        Assert.Equal(QuickSearchField.Notes, QuickSearchParser.Parse("N:secret").Groups[0].Alternatives[0].Field);
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
