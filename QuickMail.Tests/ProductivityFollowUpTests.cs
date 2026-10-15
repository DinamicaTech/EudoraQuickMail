using System.IO;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Tests;

public sealed class ProductivityFollowUpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EQM-followup-" + Guid.NewGuid().ToString("N"));
    private readonly ProfileContext _profile;
    private readonly LocalStoreService _store;
    private readonly AccountService _accounts;
    private readonly AccountModel _account = new() { Id = Guid.NewGuid(), Username = "me@example.com", IsActive = true, BackendKind = BackendKind.Pop3Smtp };
    public ProductivityFollowUpTests()
    {
        Directory.CreateDirectory(_root);
        _profile = new(_root);
        _store = new(_profile); _store.Initialize();
        _accounts = new(_profile); _accounts.SaveAccounts([_account]);
    }

    [Fact]
    public void AttachmentWithRetainedBytesDoesNotRequireOriginalFile()
    {
        var model = new ComposeModel { Attachments = [new() { FileName = "lost.pdf", PartSpecifier = Path.Combine(_root, "lost.pdf"), Content = [1, 2] }] };
        OutgoingAttachmentValidator.ValidateAndLoad(model);
        using var mime = MimeMessageBuilder.Build(model, _account);
        Assert.Single(mime.Attachments);
    }

    [Fact]
    public void MissingOrUnloadedAttachmentsFailWithNames()
    {
        var model = new ComposeModel { Attachments = [new() { FileName = "lost.pdf", PartSpecifier = Path.Combine(_root, "lost.pdf") }, new() { FileName = "remote.pdf", PartSpecifier = "2" }] };
        var ex = Assert.Throws<FileNotFoundException>(() => OutgoingAttachmentValidator.ValidateAndLoad(model));
        Assert.Contains("lost.pdf", ex.Message); Assert.Contains("remote.pdf", ex.Message);
    }

    [Fact]
    public void DiskAttachmentIsLoadedAndMissingCidRejected()
    {
        var path = Path.Combine(_root, "invoice.pdf"); File.WriteAllBytes(path, [1, 2, 3]);
        var model = new ComposeModel { Attachments = [new() { FileName = "invoice.pdf", PartSpecifier = path }] };
        OutgoingAttachmentValidator.ValidateAndLoad(model);
        Assert.Equal(3, model.Attachments[0].Content!.Length);
        model.HtmlBody = "<img src='cid:missing'>";
        Assert.Throws<FileNotFoundException>(() => OutgoingAttachmentValidator.ValidateAndLoad(model));
    }

    [Theory]
    [InlineData("recipient@example.com", "In-Reply-To: <original@example.com>", true)]
    [InlineData("recipient@example.com", "References: <other@example.com> <original@example.com>", true)]
    [InlineData("recipient@example.com", "Subject: Re: original", false)]
    [InlineData("stranger@example.com", "In-Reply-To: <original@example.com>", false)]
    [InlineData("recipient@example.com", "In-Reply-To: <original@example.com>\r\nAuto-Submitted: auto-replied", false)]
    [InlineData("me@example.com", "In-Reply-To: <original@example.com>", false)]
    public void ReplyMatchingIsConservative(string from, string headers, bool expected)
    {
        var tracking = new FollowUpTracking("original@example.com", "recipient@example.com, me@example.com", DateTimeOffset.UtcNow.AddDays(-1));
        Assert.Equal(expected, FollowUpReminder.IsResponse(tracking, new MailMessageDetail { From = from, Date = DateTimeOffset.UtcNow, RawHeaders = headers }, "me@example.com"));
    }

    [Fact]
    public async Task SentFollowUpIsIdempotentAndRetainsLinkAndIdentityAfterEditing()
    {
        using var queue = Queue(new CountingSender());
        var original = Original();
        await queue.ScheduleFollowUpAfterSentAsync(original, _account, DateTimeOffset.UtcNow);
        await queue.ScheduleFollowUpAfterSentAsync(original, _account, DateTimeOffset.UtcNow);
        var pending = Assert.Single(await queue.GetSnapshotAsync());
        Assert.StartsWith("Follow up:", pending.Message.Subject);
        Assert.Contains(Uri.EscapeDataString(original.OutgoingMessageId), pending.Message.Body);
        using var vm = new ComposeViewModel(new StubSmtpService(), _accounts, new StubCredentialService(), new StubImapMailService(), new StubTemplateService());
        vm.Seed(pending.Message); vm.Subject = "Changed";
        var changed = vm.BuildComposeModel(_account.Id);
        Assert.Equal(pending.Message.FollowUp, changed.FollowUp);
        await queue.ReplaceAsync(pending.Id, changed, DateTimeOffset.UtcNow.AddDays(2));
        Assert.Equal("Changed", Assert.Single(await queue.GetSnapshotAsync()).Message.Subject);
    }

    [Fact]
    public async Task ReceivedReplyCancelsPendingReminderWithoutSending()
    {
        var sender = new CountingSender();
        using var queue = Queue(sender);
        var original = Original();
        await queue.ScheduleFollowUpAfterSentAsync(original, _account, DateTimeOffset.UtcNow.AddHours(-1));
        await _store.SaveLocalMessageAsync(new MailMessageDetail { AccountId = _account.Id, MessageId = "reply", FolderName = "In",
            From = "recipient@example.com", To = _account.Username, Date = DateTimeOffset.UtcNow,
            RawHeaders = $"In-Reply-To: <{original.OutgoingMessageId}>\r\n", PlainTextBody = "Yes" });
        await queue.DispatchDueAsync();
        Assert.Empty(await queue.GetSnapshotAsync()); Assert.Equal(0, sender.Count);
    }

    [Fact]
    public async Task DueReminderDefersOnSyncFailureThenSendsOnce()
    {
        var sender = new CountingSender();
        using var queue = Queue(sender);
        await queue.ScheduleFollowUpAfterSentAsync(Original(), _account, DateTimeOffset.UtcNow.AddDays(-8));
        queue.SyncBeforeFollowUpAsync = (_, _) => throw new IOException("offline");
        await queue.DispatchDueAsync();
        Assert.Equal(0, sender.Count); Assert.Single(await queue.GetSnapshotAsync());
        queue.SyncBeforeFollowUpAsync = (_, _) => Task.CompletedTask;
        await queue.DispatchDueAsync(manual: true);
        await queue.DispatchDueAsync(manual: true);
        Assert.Equal(1, sender.Count); Assert.Empty(await queue.GetSnapshotAsync());
    }

    [Fact]
    public async Task SendingOriginalCreatesFollowUpOnlyAfterSuccessfulTransport()
    {
        var sender = new CountingSender();
        using var queue = Queue(sender);
        var original = Original();
        await queue.ScheduleAsync(original, DateTimeOffset.UtcNow.AddMilliseconds(100));
        Assert.Null(Assert.Single(await queue.GetSnapshotAsync()).Message.FollowUp);
        await Task.Delay(150);
        await queue.DispatchDueAsync();
        var reminder = Assert.Single(await queue.GetSnapshotAsync());
        Assert.NotNull(reminder.Message.FollowUp); Assert.Equal(1, sender.Count);
        using var mail = new LocalMailService(_store, _accounts);
        var sentFolder = (await mail.GetFoldersAsync(_account.Id)).First(f => f.Kind == SpecialFolderKind.Sent);
        var sent = Assert.Single(await mail.GetMessageSummariesAsync(_account.Id, sentFolder.FullName, 10));
        Assert.Equal(original.OutgoingMessageId, sent.InternetMessageId);
    }

    [Fact]
    public void FavoritesPersistAndInvalidConfigIsIgnored()
    {
        var config = new ConfigService(_profile);
        var model = config.Load(); model.FavoriteFolderKeys = ["F:account:LocalFolder:123"];
        config.Save(model);
        Assert.Equal(model.FavoriteFolderKeys, new ConfigService(_profile).Load().FavoriteFolderKeys);
    }

    [Fact]
    public void FavoriteIsOnlyAShortcutAndCanBeRemovedWithoutRemovingFolder()
    {
        using var vm = new MainViewModel(new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            new StubLocalStoreService(), new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());
        vm.Accounts.Add(_account);
        typeof(MainViewModel).GetMethod("BuildFolderTree", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, null);
        var original = vm.FolderTree.First(n => n.Folder != null);
        vm.ToggleFavoriteFolder(original);
        Assert.Equal("Favorites", vm.FolderTree[0].Label);
        var shortcut = Assert.Single(vm.FolderTree[0].Children);
        Assert.True(shortcut.IsFavorite);
        Assert.Same(vm.FolderTree.Skip(1).First(n => n.Folder != null).Folder, shortcut.Folder);
        vm.ToggleFavoriteFolder(shortcut);
        Assert.DoesNotContain(vm.FolderTree, n => n.Label == "Favorites");
        Assert.Contains(vm.FolderTree, n => n.Folder?.FullName == original.Folder!.FullName);
    }

    private ComposeModel Original() => new() { AccountId = _account.Id, To = "recipient@example.com", Subject = "Proposal", Body = "Please reply", FollowUpDays = 7 };
    [Fact]
    public void ConditionalReminderSurvivesLocalAndMimeDraftHeaders()
    {
        var model = FollowUpReminder.Build(Original(), _account, DateTimeOffset.UtcNow);
        Assert.Equal(model.FollowUp, FollowUpReminder.ReadDraftTracking(FollowUpReminder.DraftHeaders(model)));
        using var mime = MimeMessageBuilder.Build(model, _account);
        using var serialized = new MemoryStream();
        mime.WriteTo(serialized);
        Assert.Equal(model.FollowUp, FollowUpReminder.ReadDraftTracking(System.Text.Encoding.UTF8.GetString(serialized.ToArray())));
        Assert.Null(FollowUpReminder.ReadDraftTracking("X-QuickMail-FollowUp-Tracking: invalid"));
        Assert.True(FollowUpReminder.IsResponse(new("<original@example.com>", "recipient@example.com", DateTimeOffset.UtcNow.AddDays(-1)),
            new MailMessageDetail { From = "recipient@example.com", Date = DateTimeOffset.UtcNow, RawHeaders = "In-Reply-To: <original@example.com>" }, _account.Username));
    }

    [Fact]
    public async Task MissingScheduledAttachmentPreventsTransportAndAutomaticRetry()
    {
        var sender = new CountingSender();
        using var queue = Queue(sender);
        var original = Original();
        original.Attachments.Add(new() { FileName = "lost.pdf", PartSpecifier = Path.Combine(_root, "lost.pdf") });
        await queue.ScheduleAsync(original, DateTimeOffset.UtcNow.AddMilliseconds(100));
        await Task.Delay(150);
        await queue.DispatchDueAsync();
        var pending = Assert.Single(await queue.GetSnapshotAsync());
        Assert.Equal(0, sender.Count);
        Assert.False(pending.AutomaticRetry);
        Assert.Null(pending.Message.FollowUp);
        Assert.Contains("lost.pdf", pending.LastError);
    }

    private ScheduledSendService Queue(CountingSender sender) => new(_profile, sender, _accounts, new StubCredentialService(), _store, new LocalMailService(_store, _accounts));
    [Fact]
    public async Task Pop3FollowUpCheckReallyReceivesAndPropagatesFailure()
    {
        var transport = new FollowUpReceiver();
        using var receiver = new PeriodicPop3Receiver(_accounts, transport, interval: TimeSpan.FromHours(1));
        await receiver.CheckForFollowUpAsync(_account, CancellationToken.None);
        Assert.Equal(1, transport.ManualChecks);
        transport.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => receiver.CheckForFollowUpAsync(_account, CancellationToken.None));
        Assert.False(receiver.IsRunning);
    }

    private sealed class FollowUpReceiver : IPop3ReceiveService
    {
        public int ManualChecks { get; private set; }
        public bool Fail { get; set; }
        public Task<Pop3ReceiveResult> CheckAutomaticallyAsync(AccountModel account, CancellationToken ct = default) => Task.FromResult(new Pop3ReceiveResult(0, 0, 0, true));
        public Task<Pop3ReceiveResult> CheckNowAsync(AccountModel account, CancellationToken ct = default)
        {
            ManualChecks++;
            if (Fail) throw new IOException("Cannot connect");
            return Task.FromResult(new Pop3ReceiveResult(0, 0, 0, false));
        }
    }

    private sealed class CountingSender : ISendMailService
    {
        public int Count { get; private set; }
        public Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default) { Count++; return Task.CompletedTask; }
        public Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password, string organizerEmail, CancellationToken ct = default) => Task.CompletedTask;
        public Task VerifyAsync(AccountModel account, string? password, CancellationToken ct = default) => Task.CompletedTask;
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
