using System.Text;
using System.IO;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Tests;

public class RuleEnhancementTests
{
    [Fact]
    public void SelectedMessagePreviewRespectsRuleAccountScope()
    {
        var account = Guid.NewGuid();
        var service = new RuleService(new StubImapMailService(), new StubLocalStoreService(), Path.GetTempPath());
        var rule = new MailRule { AccountId = account, FromContains = "alice" };
        var expected = new MailMessageSummary { MessageId = "1", AccountId = account, From = "alice@example.com" };
        var otherAccount = new MailMessageSummary { MessageId = "2", AccountId = Guid.NewGuid(), From = "alice@example.com" };
        Assert.Same(expected, Assert.Single(service.TestRule(rule, [expected, otherAccount])));
    }

    [Fact]
    public void NewRuleFieldsRoundTripWithoutChangingLegacyActionNumbers()
    {
        var original = new MailRule { ActiveUntil = DateTime.Today.AddDays(14), ReplyTemplateId = 17, Action = RuleAction.ReplyTemplate };
        var restored = System.Text.Json.JsonSerializer.Deserialize<MailRule>(System.Text.Json.JsonSerializer.Serialize(original))!;
        Assert.Equal(original.ActiveUntil, restored.ActiveUntil);
        Assert.Equal(17, restored.ReplyTemplateId);
        Assert.Equal(RuleAction.ReplyTemplate, restored.Action);
        Assert.Equal(4, (int)RuleAction.Forward);
        Assert.Equal(3, (int)RuleAction.Delete);
    }

    [Fact]
    public void ExpirationIsInclusiveAndDoesNotDeleteRule()
    {
        var rule = new MailRule { ActiveUntil = DateTime.Today };
        Assert.False(rule.IsExpired);
        rule.ActiveUntil = DateTime.Today.AddDays(-1);
        Assert.True(rule.IsExpired);
        Assert.True(rule.IsEnabled);
        rule.ActiveUntil = null;
        Assert.False(rule.IsExpired);
    }

    [Fact]
    public async Task ExpiredRuleDoesNotExecuteEvenWhenAppliedDirectly()
    {
        var service = new RuleService(new StubImapMailService(), new StubLocalStoreService(), Path.GetTempPath());
        var called = false;
        service.ReplyTemplateAsync = (_, _, _) => { called = true; return Task.CompletedTask; };
        var rule = new MailRule { Action = RuleAction.ReplyTemplate, ActiveUntil = DateTime.Today.AddDays(-1) };
        var message = new MailMessageSummary { MessageId = "1", From = "a@example.com" };
        Assert.False(service.IsMatch(rule, message));
        Assert.Empty(service.TestRule(rule, [message]));
        var result = await service.ApplyRuleToMessagesAsync(rule, [message], new StubLocalStoreService(), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.MatchedCount);
        Assert.False(called);
    }

    [Theory]
    [InlineData("Auto-Submitted: auto-replied")]
    [InlineData("Auto-Submitted: auto-generated")]
    [InlineData("Precedence: bulk")]
    [InlineData("List-Id: newsletter.example.com")]
    [InlineData("List-Unsubscribe: <https://example.com/unsubscribe>")]
    [InlineData("Return-Path: <>")]
    [InlineData("X-Auto-Response-Suppress: All")]
    [InlineData("X-QuickMail-Rule-Forward: true")]
    public void AutomatedMessagesCannotStartReplyLoops(string header)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(header + "\r\n\r\n"));
        using var mime = MimeMessage.Load(stream);
        Assert.True(RuleReplyTemplateService.IsAutomaticOrBulk(mime));
    }

    [Fact]
    public void HumanMailIsNotRejected()
    {
        using var message = new MimeMessage();
        message.Headers.Add("Auto-Submitted", "no");
        Assert.False(RuleReplyTemplateService.IsAutomaticOrBulk(message));
    }

    [Fact]
    public void PreviewHasCountsExamplesAndNoSendClaim()
    {
        var rule = new MailRule { Action = RuleAction.ReplyTemplate, AlsoMarkAsRead = false };
        var matches = Enumerable.Range(0, 30).Select(i => new MailMessageSummary { MessageId = i.ToString(), Direction = MessageDirection.Incoming }).ToList();
        var result = RulePreviewBuilder.Build(rule, matches, matches, "In");
        Assert.Equal(30, result.MatchCount);
        Assert.Equal(20, result.SampleMessages.Count);
        Assert.Equal(0, result.MarkReadCount);
        Assert.Contains("not guaranteed sends", result.ActionDescription);
    }
}
