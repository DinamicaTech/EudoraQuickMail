using System.IO;
using Microsoft.Data.Sqlite;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class LocalFirstMailTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "quickmail-local-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DpapiSecrets_RoundTripForCurrentWindowsUser()
    {
        var protector = new DpapiAccountSecretProtector();
        var encrypted = protector.Protect("not-plain-text");
        Assert.DoesNotContain("not-plain-text", encrypted);
        Assert.Equal("not-plain-text", protector.Unprotect(encrypted));
        Assert.Null(protector.Unprotect("not base64"));
    }

    [Fact]
    public async Task LocalStore_IndexesMovesAndDeletesAuthoritativeMessage()
    {
        var accountId = Guid.NewGuid();
        var store = CreateStore();
        await store.SaveFoldersAsync(accountId,
        [
            Folder(accountId, "Inbox", container: false),
            Folder(accountId, "Clients", container: true),
            Folder(accountId, "Clients/Acme", container: false),
        ]);
        var message = Message(accountId, "Inbox", "m1", "DeporWin invoice body");
        await store.SavePop3MessageAsync("uid-1", message);

        Assert.True(await store.ContainsPop3UidAsync(accountId, "uid-1"));
        var found = await store.SearchLocalMessagesAsync(new LocalSearchQuery("DeporWin", accountId));
        Assert.Equal(1, found.TotalMatches);
        Assert.Single(found.Messages);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MoveLocalMessagesAsync(accountId, "Inbox", "Clients", ["m1"]));
        await store.MoveLocalMessagesAsync(accountId, "Inbox", "Clients/Acme", ["m1"]);
        Assert.Empty((await store.SearchLocalMessagesAsync(
            new LocalSearchQuery("DeporWin", accountId, "Inbox"))).Messages);
        Assert.Single((await store.SearchLocalMessagesAsync(
            new LocalSearchQuery("DeporWin", accountId, "Clients/Acme"))).Messages);

        await store.DeleteLocalMessagesAsync(accountId, "Clients/Acme", ["m1"]);
        Assert.Equal(0, (await store.SearchLocalMessagesAsync(new LocalSearchQuery("DeporWin", accountId))).TotalMatches);
        Assert.True(await store.ContainsPop3UidAsync(accountId, "uid-1"));
    }

    [Fact]
    public async Task LocalStore_EnforcesRenderedMessageCap()
    {
        var accountId = Guid.NewGuid();
        var store = CreateStore();
        for (var i = 0; i < LocalMailConstants.MaxRenderedMessages + 5; i++)
            await store.SaveLocalMessageAsync(Message(accountId, "Inbox", $"m{i}", "commonprefix"));

        var found = await store.SearchLocalMessagesAsync(new LocalSearchQuery("commonprefix", Limit: 50_000));
        Assert.Equal(LocalMailConstants.MaxRenderedMessages + 5, found.TotalMatches);
        Assert.Equal(LocalMailConstants.MaxRenderedMessages, found.Messages.Count);

        var secondPage = await store.LoadLocalPageAsync(accountId, "Inbox",
            LocalMailConstants.MaxRenderedMessages, LocalMailConstants.MaxRenderedMessages);
        Assert.Equal(LocalMailConstants.MaxRenderedMessages + 5, secondPage.TotalMatches);
        Assert.Equal(5, secondPage.Messages.Count);
    }

    [Fact]
    public async Task Pop3Receiver_CommitsLocallyBeforeDeletingAndRediscardsKnownUidl()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), BackendKind = BackendKind.Pop3Smtp, Username = "user@example.test",
            Pop3Host = "mail.test", EncryptedPop3Password = "encrypted",
        };
        var events = new List<string>();
        var store = new RecordingStore(events);
        var transport = new FakePop3Transport(events,
        [
            ("new-uid", CreateMime("new")),
            ("known-uid", CreateMime("known")),
        ]);
        store.Known.Add("known-uid");
        var service = new Pop3ReceiveService(new FakeTransportFactory(transport), store, new FakeProtector());

        var result = await service.CheckNowAsync(account);

        Assert.Equal(1, result.Downloaded);
        Assert.Equal(1, result.RemovedAlreadyKnown);
        Assert.True(events.IndexOf("save:new-uid") < events.IndexOf("delete:0"));
        Assert.Equal("disconnect:commit", events[^1]);
    }

    [Fact]
    public async Task AutomaticPop3Check_RespectsPerAccountFlag()
    {
        var account = new AccountModel { BackendKind = BackendKind.Pop3Smtp, CheckIncomingMail = false };
        var factory = new ThrowingTransportFactory();
        var service = new Pop3ReceiveService(factory, new RecordingStore([]), new FakeProtector());
        var result = await service.CheckAutomaticallyAsync(account);
        Assert.True(result.Skipped);
        Assert.False(factory.WasCalled);
    }

    private LocalStoreService CreateStore()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory));
        store.Initialize();
        return store;
    }

    private static MailFolderModel Folder(Guid accountId, string name, bool container) => new()
    { AccountId = accountId, FullName = name, DisplayName = name, IsContainer = container };

    private static MailMessageDetail Message(Guid accountId, string folder, string id, string body) => new()
    {
        AccountId = accountId, FolderName = folder, MessageId = id, InternetMessageId = $"<{id}@test>",
        From = "sender@example.test", To = "receiver@example.test", Cc = "copy@example.test",
        Subject = "Subject", Date = DateTimeOffset.UtcNow, PlainTextBody = body, Preview = body,
    };

    private static MimeMessage CreateMime(string subject)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("sender@example.test"));
        message.To.Add(MailboxAddress.Parse("receiver@example.test"));
        message.Subject = subject;
        message.Date = DateTimeOffset.UtcNow;
        message.Body = new TextPart("plain") { Text = "body" };
        return message;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeProtector : IAccountSecretProtector
    {
        public string Protect(string secret) => "encrypted";
        public string? Unprotect(string protectedSecret) => "password";
        public void SetPop3Password(AccountModel account, string password) { }
        public void SetSmtpPassword(AccountModel account, string password) { }
        public string? GetPop3Password(AccountModel account) => "password";
        public string? GetSmtpPassword(AccountModel account) => "password";
    }

    private sealed class FakeTransportFactory(IPop3Transport transport) : IPop3TransportFactory
    { public IPop3Transport Create(AccountModel account) => transport; }

    private sealed class ThrowingTransportFactory : IPop3TransportFactory
    {
        public bool WasCalled { get; private set; }
        public IPop3Transport Create(AccountModel account) { WasCalled = true; throw new InvalidOperationException(); }
    }

    private sealed class FakePop3Transport(List<string> events, IReadOnlyList<(string Uidl, MimeMessage Message)> mail)
        : IPop3Transport
    {
        public bool SupportsUidListing => true;
        public Task ConnectAsync(AccountModel account, CancellationToken ct) { events.Add("connect"); return Task.CompletedTask; }
        public Task AuthenticateAsync(string username, string password, CancellationToken ct) { events.Add("auth"); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> GetMessageUidsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(mail.Select(m => m.Uidl).ToList());
        public Task<MimeMessage> GetMessageAsync(int index, CancellationToken ct) => Task.FromResult(mail[index].Message);
        public Task DeleteMessageAsync(int index, CancellationToken ct) { events.Add($"delete:{index}"); return Task.CompletedTask; }
        public Task DisconnectAsync(bool commitDeletes, CancellationToken ct)
        { events.Add(commitDeletes ? "disconnect:commit" : "disconnect:rollback"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingStore(List<string> events) : ILocalMailboxStore
    {
        public HashSet<string> Known { get; } = [];
        public Task<bool> ContainsPop3UidAsync(Guid accountId, string uidl, CancellationToken ct = default) =>
            Task.FromResult(Known.Contains(uidl));
        public Task SavePop3MessageAsync(string uidl, MailMessageDetail message, CancellationToken ct = default)
        { events.Add($"save:{uidl}"); Known.Add(uidl); return Task.CompletedTask; }
        public Task SaveLocalMessageAsync(MailMessageDetail message, CancellationToken ct = default) => Task.CompletedTask;
        public Task<LocalSearchResult> SearchLocalMessagesAsync(LocalSearchQuery query, CancellationToken ct = default) =>
            Task.FromResult(new LocalSearchResult([], 0));
        public Task<LocalSearchResult> LoadLocalPageAsync(Guid? accountId, string? folderName, int limit, int offset,
            LocalSearchSort sort = LocalSearchSort.NewestFirst, CancellationToken ct = default) =>
            Task.FromResult(new LocalSearchResult([], 0));
        public Task MoveLocalMessagesAsync(Guid accountId, string sourceFolder, string destinationFolder,
            IReadOnlyCollection<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteLocalMessagesAsync(Guid accountId, string folderName,
            IReadOnlyCollection<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
    }
}
