using System.IO;
using System.Text.Json;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Tests;

public sealed class RepeatedSendingAndForwardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EQM-repetition-" + Guid.NewGuid().ToString("N"));
    private readonly ProfileContext _profile;
    private readonly LocalStoreService _store;
    private readonly AccountService _accounts;
    private readonly AccountModel _account = new() { Id = Guid.NewGuid(), Username = "me@example.com", BackendKind = BackendKind.Pop3Smtp, IsActive = true };
    public RepeatedSendingAndForwardTests()
    {
        Directory.CreateDirectory(_root); _profile = new(_root);
        _store = new(_profile); _store.Initialize();
        _accounts = new(_profile); _accounts.SaveAccounts([_account]);
    }

    [Fact]
    public void NextSkipsMissedDatesButPreservesOriginalCadenceAndInclusiveEnd()
    {
        var plan = new RepeatedSendSchedule(3, new DateOnly(2026, 10, 13), "UTC");
        var prior = RepeatedSending.AtEight(new DateOnly(2026, 10, 1), "UTC");
        Assert.Equal(RepeatedSending.AtEight(new DateOnly(2026, 10, 13), "UTC"),
            RepeatedSending.Next(plan, prior, new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero)));
        Assert.Null(RepeatedSending.Next(plan, prior, new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void LocalEightSurvivesDaylightSavingChange()
    {
        var plan = new RepeatedSendSchedule(1, new DateOnly(2026, 10, 27), "Romance Standard Time");
        var prior = RepeatedSending.AtEight(new DateOnly(2026, 10, 24), plan.TimeZoneId);
        var next = RepeatedSending.Next(plan, prior, prior)!.Value;
        Assert.Equal(TimeSpan.FromHours(25), next - prior);
        Assert.Equal(8, TimeZoneInfo.ConvertTime(next, TimeZoneInfo.FindSystemTimeZoneById(plan.TimeZoneId)).Hour);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(36501)]
    public void InvalidIntervalsAreRejected(int days) => Assert.Throws<ArgumentException>(() =>
        RepeatedSending.Validate(new(days, new DateOnly(2030, 1, 1), "UTC"), RepeatedSending.AtEight(new DateOnly(2027, 1, 1), "UTC")));

    private ScheduledMail Due(bool accepted = false, bool finished = false)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var model = new ComposeModel { AccountId = _account.Id, To = "recipient@example.com", Subject = "Repetition: Test", Body = "Same body",
            Repetition = new(2, finished ? today.AddDays(-1) : today.AddDays(20), "UTC") };
        return new(Guid.NewGuid(), RepeatedSending.AtEight(today.AddDays(-9), "UTC"), model,
            TransportAccepted: accepted, AcceptedAtUtc: accepted ? DateTimeOffset.UtcNow : null);
    }
    private Task Seed(ScheduledMail item) => File.WriteAllTextAsync(Path.Combine(_root, "scheduled-mail.json"), JsonSerializer.Serialize(new[] { item }));
    private ScheduledSendService Queue(Sender sender, MailActivityPolicyService? activity = null) =>
        new(_profile, sender, _accounts, new StubCredentialService(), _store, new LocalMailService(_store, _accounts), activity);

    [Fact]
    public async Task OverdueOccurrenceSendsOnceThenQueuesOnlyOneFutureCopy()
    {
        var old = Due(); await Seed(old);
        var sender = new Sender(); using var queue = Queue(sender);
        await queue.DispatchDueAsync(); await queue.DispatchDueAsync();
        Assert.Equal(1, sender.Count);
        var next = Assert.Single(await queue.GetSnapshotAsync());
        Assert.True(next.SendAtUtc > DateTimeOffset.UtcNow);
        Assert.Equal(old.Message.Body, next.Message.Body);
        Assert.Equal(old.Message.Subject, next.Message.Subject);
        Assert.NotEqual(old.Message.OutgoingMessageId, next.Message.OutgoingMessageId);
        Assert.Equal(old.Message.Repetition, next.Message.Repetition);
        await queue.RemoveAsync(next.Id, true);
        await queue.DispatchDueAsync(); Assert.Empty(await queue.GetSnapshotAsync());
    }

    [Fact]
    public async Task FailedOccurrenceDoesNotCreateNextCopy()
    {
        var old = Due(); await Seed(old);
        using var queue = Queue(new Sender { Fail = true });
        await queue.DispatchDueAsync();
        var pending = Assert.Single(await queue.GetSnapshotAsync());
        Assert.Equal(old.Id, pending.Id); Assert.False(pending.TransportAccepted); Assert.NotNull(pending.LastError);
    }

    [Fact]
    public async Task AcceptedOccurrenceResumesWithoutSendingAgain()
    {
        await Seed(Due(accepted: true));
        var sender = new Sender(); using var queue = Queue(sender);
        await queue.DispatchDueAsync(); await queue.DispatchDueAsync();
        Assert.Equal(0, sender.Count); Assert.Single(await queue.GetSnapshotAsync());
    }

    [Fact]
    public async Task ReopeningAfterEndSendsOneOverdueCopyAndEndsSeries()
    {
        await Seed(Due(finished: true));
        var sender = new Sender(); using var queue = Queue(sender);
        await queue.DispatchDueAsync();
        Assert.Equal(1, sender.Count); Assert.Empty(await queue.GetSnapshotAsync());
    }

    [Fact]
    public void DraftAndMimePreserveRepetitionPlan()
    {
        var model = Due().Message;
        Assert.Equal(model.Repetition, RepeatedSending.ReadHeaders(FollowUpReminder.DraftHeaders(model)));
        using var mime = MimeMessageBuilder.Build(model, _account);
        using var buffer = new MemoryStream(); mime.WriteTo(buffer);
        Assert.Equal(model.Repetition, RepeatedSending.ReadHeaders(System.Text.Encoding.UTF8.GetString(buffer.ToArray())));
        using var vm = new ComposeViewModel(new StubSmtpService(), _accounts, new StubCredentialService(), new StubImapMailService(), new StubTemplateService());
        vm.Seed(model);
        Assert.Equal(model.Repetition, vm.BuildComposeModel(_account.Id).Repetition);
    }

    [Fact]
    public async Task ComposeCommandSchedulesAtEightWithPrefixWithoutSendingNow()
    {
        var sender = new Sender(); using var queue = Queue(sender);
        using var vm = new ComposeViewModel(sender, _accounts, new StubCredentialService(), new StubImapMailService(),
            new StubTemplateService(), outgoingQueue: queue);
        vm.Seed(new ComposeModel { AccountId = _account.Id, To = "recipient@example.com", Subject = "Monthly report", Body = "Report" });
        var tomorrow = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        vm.PromptRepetitionRequested = _ => Task.FromResult<RepeatedSendRequest?>(new(tomorrow, 7, tomorrow.AddDays(28)));
        await vm.SendRepeatedlyCommand.ExecuteAsync(null);
        var pending = Assert.Single(await queue.GetSnapshotAsync());
        Assert.Equal("Repetition: Monthly report", pending.Message.Subject);
        Assert.Equal(0, sender.Count);
        Assert.Equal(RepeatedSending.AtEight(tomorrow, TimeZoneInfo.Local.Id), pending.SendAtUtc);
        Assert.Equal(7, pending.Message.Repetition!.IntervalDays);
    }

    [Xunit.WpfFact]
    public void RepetitionDialogLoadsSuggestedValues()
    {
        WpfTestApplication.EnsureStarted();
        var date = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        var window = new QuickMail.Views.RepeatedSendWindow(new(date, 3, date.AddDays(30)));
        Assert.Equal("3", ((System.Windows.Controls.TextBox)window.FindName("Interval")).Text);
        Assert.Equal(date.ToDateTime(TimeOnly.MinValue), ((QuickMail.Controls.DateTimeField)window.FindName("StartDate")).Value);
        window.Close();
    }

    [Fact]
    public async Task ClosingComposerWhileChoosingDatesDoesNotScheduleMail()
    {
        var sender = new Sender(); using var queue = Queue(sender);
        using var vm = new ComposeViewModel(sender, _accounts, new StubCredentialService(), new StubImapMailService(),
            new StubTemplateService(), outgoingQueue: queue);
        vm.Seed(new ComposeModel { AccountId = _account.Id, To = "recipient@example.com", Subject = "Report", Body = "Report" });
        var choice = new TaskCompletionSource<RepeatedSendRequest?>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PromptRepetitionRequested = _ => choice.Task;
        var operation = vm.SendRepeatedlyCommand.ExecuteAsync(null);
        vm.Dispose();
        var tomorrow = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        choice.SetResult(new(tomorrow, 7, tomorrow.AddDays(28)));
        await operation;
        Assert.Empty(await queue.GetSnapshotAsync());
        Assert.Equal(0, sender.Count);
    }

    private async Task<MailMessageDetail> Original(string headers = "")
    {
        var detail = new MailMessageDetail { AccountId = _account.Id, FolderName = "In", MessageId = Guid.NewGuid().ToString(),
            InternetMessageId = Guid.NewGuid() + "@sender.example", From = "sender@example.com", To = _account.Username,
            Subject = "Invoice", PlainTextBody = "Invoice body", Date = DateTimeOffset.UtcNow, RawHeaders = headers,
            Attachments = [new AttachmentModel { FileName = "invoice.pdf", Content = [1, 2, 3], ContentType = "application/pdf" }] };
        await _store.SaveLocalMessageAsync(detail);
        return detail;
    }

    [Fact]
    public async Task ForwardKeepsOriginalAndAttachmentsAndDeduplicatesAcrossRestart()
    {
        var original = await Original();
        using var activity = new MailActivityPolicyService(_profile); activity.SetOffline();
        using var mail = new LocalMailService(_store, _accounts);
        var rule = new MailRule { Name = "Forward invoice", ForwardTo = "destination@example.com", Action = RuleAction.Forward, FromContains = "sender@example.com", AlsoMarkAsRead = false };
        using (var queue = Queue(new Sender(), activity))
        {
            var forward = new RuleForwardService(mail, _accounts, queue);
            var rules = new RuleService(mail, _store, _root, _accounts) { ForwardMessageAsync = forward.ForwardAsync };
            var result = await rules.ApplyRuleToMessagesAsync(rule, [original], _store, CancellationToken.None);
            Assert.Empty(result.RemovedMessages);
            await forward.ForwardAsync(rule, original, CancellationToken.None);
            var item = Assert.Single(await queue.GetSnapshotAsync());
            Assert.Equal("destination@example.com", item.Message.To);
            Assert.Equal("Fwd: Invoice", item.Message.Subject);
            Assert.NotNull(await _store.LoadDetailAsync(_account.Id, "In", original.MessageId));
            OutgoingAttachmentValidator.ValidateAndLoad(item.Message);
            Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(item.Message.Attachments).Content);
            using var mime = MimeMessageBuilder.Build(item.Message, _account);
            Assert.Equal("true", mime.Headers[RuleForwardService.MarkerHeader]);
            await queue.RemoveAsync(item.Id, true);
        }
        using var restarted = Queue(new Sender(), activity);
        await new RuleForwardService(mail, _accounts, restarted).ForwardAsync(rule, original, CancellationToken.None);
        Assert.Empty(await restarted.GetSnapshotAsync());
    }

    [Fact]
    public async Task ForwardBlocksOwnAddressAndSkipsLoopMarker()
    {
        using var activity = new MailActivityPolicyService(_profile); activity.SetOffline();
        using var queue = Queue(new Sender(), activity);
        using var mail = new LocalMailService(_store, _accounts);
        var forward = new RuleForwardService(mail, _accounts, queue);
        var original = await Original();
        await Assert.ThrowsAsync<InvalidOperationException>(() => forward.ForwardAsync(new MailRule { ForwardTo = _account.Username }, original, CancellationToken.None));
        var loop = await Original(RuleForwardService.MarkerHeader + ": true");
        await forward.ForwardAsync(new MailRule { ForwardTo = "elsewhere@example.com" }, loop, CancellationToken.None);
        Assert.Empty(await queue.GetSnapshotAsync());
    }

    [Fact]
    public async Task ReplyTemplateQueuesOncePerSenderAndKeepsOriginalWithoutAttachments()
    {
        using var activity = new MailActivityPolicyService(_profile); activity.SetOffline();
        using var queue = Queue(new Sender(), activity);
        using var mail = new LocalMailService(_store, _accounts);
        using var templates = new TemplateService(_profile);
        var template = await templates.AddAsync(new MessageTemplate { Title = "Vacation", Body = "Away until Monday.\nThank you." });
        var rule = new MailRule { Action = RuleAction.ReplyTemplate, ReplyTemplateId = template.Id, ActiveUntil = DateTime.Today.AddDays(7) };
        var reply = new RuleReplyTemplateService(mail, _accounts, templates, queue);
        var original = await Original();
        await reply.ReplyAsync(rule, original, TestContext.Current.CancellationToken);
        await reply.ReplyAsync(rule, await Original(), TestContext.Current.CancellationToken);
        var item = Assert.Single(await queue.GetSnapshotAsync());
        Assert.Equal("sender@example.com", item.Message.To);
        Assert.Equal("Re: Invoice", item.Message.Subject);
        Assert.Equal(template.Body, item.Message.Body);
        Assert.Equal(original.InternetMessageId, item.Message.InReplyToMessageId);
        Assert.Empty(item.Message.Attachments);
        Assert.NotNull(await _store.LoadDetailAsync(_account.Id, "In", original.MessageId));
    }

    [Theory]
    [InlineData("")] [InlineData("not-an-email")] [InlineData("a@example.com,b@example.com")] [InlineData("a@example.com\r\nBcc: hidden@example.com")]
    public void InvalidForwardAddressesAreRejected(string address) => Assert.Throws<ArgumentException>(() => RuleForwardService.ValidateDestination(address));

    [Fact]
    public void RuleEditorRequiresDestinationAndConditionButNotFolder()
    {
        var vm = new RulesManagerViewModel(new StubRuleService(), accounts: []);
        vm.NewRuleCommand.Execute(null);
        vm.SelectedRule!.Name = "Forward";
        vm.SelectedRule.Action = RuleAction.Forward;
        vm.SelectedRule.FromContains = "sender@example.com";
        vm.SaveRuleCommand.Execute(null);
        Assert.NotEmpty(vm.FolderError);
        vm.SelectedRule.ForwardTo = "destination@example.com";
        vm.SelectedRule.FromContains = null;
        vm.SaveRuleCommand.Execute(null);
        Assert.NotEmpty(vm.ConditionsError);
        vm.SelectedRule.FromContains = "sender@example.com";
        vm.SaveRuleCommand.Execute(null);
        Assert.Empty(vm.FolderError); Assert.Empty(vm.ConditionsError);
        Assert.Null(vm.SelectedRule.TargetFolder);
    }

    private sealed class Sender : ISendMailService
    {
        public int Count { get; private set; }
        public bool Fail { get; init; }
        public Task SendAsync(ComposeModel compose, AccountModel account, string? password, CancellationToken ct = default)
        { if (Fail) throw new IOException("Network unavailable"); Count++; return Task.CompletedTask; }
        public Task SendIcsReplyAsync(string content, AccountModel account, string? password, string organizer, CancellationToken ct = default) => Task.CompletedTask;
        public Task VerifyAsync(AccountModel account, string? password, CancellationToken ct = default) => Task.CompletedTask;
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
