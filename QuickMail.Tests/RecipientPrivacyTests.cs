using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Tests;

public sealed class RecipientPrivacyTests
{
    private const string Exposed = "a@one.com, b@one.com, c@two.com, d@three.com";

    [Theory]
    [InlineData("a@one.com,b@two.com,c@three.com", "", false)]
    [InlineData("a@one.com,b@one.com,c@one.com,d@ONE.com", "", false)]
    [InlineData("a@one.com,b@one.com", "c@two.com,d@three.com", true)]
    [InlineData("a@one.com,b@one.com,c@two.com", "A@ONE.com", false)]
    [InlineData("Team: a@one.com,b@one.com,c@two.com,d@three.com;", "", true)]
    [InlineData("\"Doe, Jane\" <a@one.com>; b@one.com; c@two.com; d@three.com", "", true)]
    public void DetectsOnlyMoreThanThreeUniqueVisibleMixedDomainRecipients(string to, string cc, bool expected) =>
        Assert.Equal(expected, RecipientPrivacy.Assess(to, cc).RequiresWarning);

    [Fact]
    public void ProtectPreservesAllRecipientsIncludingExistingBccAndDeduplicates()
    {
        var result = RecipientPrivacy.Protect("Me <me@own.com>", "\"Doe, Jane\" <a@one.com>, b@two.com",
            "Friends: A@ONE.com,c@three.com;", "hidden@four.com, B@TWO.com");
        Assert.Equal("me@own.com", result.To);
        Assert.Equal("a@one.com, b@two.com, c@three.com, hidden@four.com", result.Bcc);
    }

    [Fact]
    public void InvalidBccCannotBeSilentlyDiscarded() => Assert.Throws<FormatException>(() =>
        RecipientPrivacy.Protect("me@own.com", Exposed, "", "\"unclosed"));

    [Theory]
    [InlineData("send")] [InlineData("immediately")] [InlineData("later")] [InlineData("repeat")]
    public async Task AllSendEntryPointsCancelWithoutSendingOrQueueing(string action)
    {
        var smtp = new StubSmtpService(); var queue = new Queue();
        using var vm = Create(smtp, queue);
        var prompts = 0;
        vm.RecipientPrivacyRequested = _ => { prompts++; return RecipientPrivacyChoice.Cancel; };
        await Run(vm, action);
        Assert.Equal(1, prompts); Assert.Empty(smtp.Sent); Assert.Empty(queue.Messages);
        Assert.Equal(Exposed, vm.To);
    }

    [Theory]
    [InlineData("send")] [InlineData("immediately")] [InlineData("later")] [InlineData("repeat")]
    public async Task AllSendEntryPointsApplyPrivacyRepairBeforeSending(string action)
    {
        var smtp = new StubSmtpService(); var queue = new Queue();
        using var vm = Create(smtp, queue);
        vm.Bcc = "hidden@four.com";
        vm.RecipientPrivacyRequested = _ => RecipientPrivacyChoice.ProtectWithBcc;
        await Run(vm, action);
        var message = Assert.Single(queue.Messages.Concat(smtp.Sent.Select(s => s.Compose)));
        Assert.Equal("me@own.com", message.To); Assert.Empty(message.Cc);
        Assert.Contains("hidden@four.com", message.Bcc);
        Assert.Contains("d@three.com", message.Bcc);
    }

    [Fact]
    public async Task ExplicitKeepVisibleChoiceSendsUnchanged()
    {
        var smtp = new StubSmtpService(); var queue = new Queue();
        using var vm = Create(smtp, queue);
        vm.RecipientPrivacyRequested = _ => RecipientPrivacyChoice.KeepVisible;
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(Exposed, Assert.Single(queue.Messages).To);
    }

    [Fact]
    public async Task MissingPromptCancelsRatherThanSilentlyExposingAddresses()
    {
        var smtp = new StubSmtpService(); var queue = new Queue();
        using var vm = Create(smtp, queue);
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Empty(queue.Messages); Assert.Empty(smtp.Sent);
    }

    [Fact]
    public async Task RepetitionChecksRecipientChangesMadeWhileDatePromptIsOpen()
    {
        var smtp = new StubSmtpService(); var queue = new Queue();
        using var vm = Create(smtp, queue);
        vm.To = "one@one.com";
        var date = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        var pending = new TaskCompletionSource<RepeatedSendRequest?>();
        vm.PromptRepetitionRequested = _ => pending.Task;
        var prompts = 0;
        vm.RecipientPrivacyRequested = _ => { prompts++; return RecipientPrivacyChoice.Cancel; };
        var send = vm.SendRepeatedlyCommand.ExecuteAsync(null);
        Assert.Equal(0, prompts);
        vm.To = Exposed;
        pending.SetResult(new(date, 7, date.AddDays(30)));
        await send;
        Assert.Equal(1, prompts); Assert.Empty(queue.Messages); Assert.Empty(smtp.Sent);
    }

    [Fact]
    public async Task RepetitionPrivacyRepairUsesCurrentSenderAfterModelessPrompt()
    {
        var smtp = new StubSmtpService(); var queue = new Queue();
        using var vm = Create(smtp, queue);
        var date = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        var pending = new TaskCompletionSource<RepeatedSendRequest?>();
        vm.PromptRepetitionRequested = _ => pending.Task;
        vm.RecipientPrivacyRequested = _ => RecipientPrivacyChoice.ProtectWithBcc;
        var send = vm.SendRepeatedlyCommand.ExecuteAsync(null);
        var updatedAccount = new AccountModel { Id = Guid.NewGuid(), Username = "new@own.com", BackendKind = BackendKind.Pop3Smtp };
        vm.SenderAccount = updatedAccount;
        pending.SetResult(new(date, 7, date.AddDays(30)));
        await send;
        var message = Assert.Single(queue.Messages);
        Assert.Equal(updatedAccount.Id, message.AccountId); Assert.Equal(updatedAccount.Username, message.To);
    }

    private static ComposeViewModel Create(StubSmtpService smtp, Queue queue)
    {
        var vm = new ComposeViewModel(smtp, new StubAccountService(), new StubCredentialService(),
            new StubImapMailService(), new StubTemplateService(), outgoingQueue: queue);
        vm.SenderAccount = new AccountModel { Id = Guid.NewGuid(), Username = "me@own.com", BackendKind = BackendKind.Pop3Smtp, IsActive = true };
        vm.To = Exposed; vm.Body = "Hello";
        vm.PromptScheduleTimeRequested = _ => DateTime.Now.AddDays(1);
        var date = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        vm.PromptRepetitionRequested = _ => Task.FromResult<RepeatedSendRequest?>(new(date, 7, date.AddDays(30)));
        return vm;
    }

    private static Task Run(ComposeViewModel vm, string action) => action switch
    {
        "immediately" => vm.SendImmediatelyCommand.ExecuteAsync(null),
        "later" => vm.ScheduleSendCommand.ExecuteAsync(null),
        "repeat" => vm.SendRepeatedlyCommand.ExecuteAsync(null),
        _ => vm.SendCommand.ExecuteAsync(null)
    };

    private sealed class Queue : IOutgoingMailQueue
    {
        public List<ComposeModel> Messages { get; } = [];
        public Task<Guid> QueueImmediateAsync(ComposeModel message, Guid? replaceScheduledId = null, DateTimeOffset? notBeforeUtc = null, CancellationToken ct = default)
        { Messages.Add(message); return Task.FromResult(Guid.NewGuid()); }
        public Task<Guid> ScheduleAsync(ComposeModel message, DateTimeOffset sendAtUtc, CancellationToken ct = default)
        { Messages.Add(message); return Task.FromResult(Guid.NewGuid()); }
        public Task ReplaceAsync(Guid id, ComposeModel message, DateTimeOffset sendAtUtc, CancellationToken ct = default)
        { Messages.Add(message); return Task.CompletedTask; }
        public Task<ComposeModel?> UndoSendAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ComposeModel?>(null);
    }
}
