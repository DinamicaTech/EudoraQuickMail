using System.IO;
using Microsoft.Data.Sqlite;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class SpamTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "eqm-spam-" + Guid.NewGuid().ToString("N"));
    private LocalStoreService Store()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory));
        store.Initialize();
        return store;
    }
    private static MailMessageSummary Message(string id = "1", string folder = "In") => new()
    {
        AccountId = Guid.Parse("A0B6E107-B8C6-4852-BCB8-47C90AC743E0"), MessageId = id,
        FolderName = folder, From = "Offers <news@example.com>", To = "user@example.org",
        Subject = "Offer", Date = DateTimeOffset.UtcNow.AddYears(-2),
        InternetMessageId = "test-" + id + "@example.com", Direction = MessageDirection.Incoming
    };

    [Theory]
    [InlineData("gmail.com")]
    [InlineData("GOOGLEMAIL.COM")]
    [InlineData("hotmail.es")]
    [InlineData("yahoo.com.au")]
    [InlineData("outlook.fr")]
    [InlineData("sub.gmail.com")]
    [InlineData("icloud.com")]
    [InlineData("proton.me")]
    [InlineData("bad domain.com")]
    public void SharedAndInvalidDomainsCannotBeSaved(string domain)
    {
        Assert.False(SpamPolicy.CanBlockDomain(domain));
        Assert.Throws<InvalidOperationException>(() => SpamPolicy.Validate(
            new MailRule { IsSpamFilter = true, SpamBlockDomain = true, FromContains = domain }));
    }

    [Fact]
    public void ExactAddressIgnoresAliasAndDoesNotMatchSubstringOrOutgoing()
    {
        var rule = new MailRule { IsSpamFilter = true, FromContains = "news@example.com" };
        var message = Message();
        Assert.True(SpamPolicy.Matches(rule, message));
        message.From = "\"news@example.com\" <good@example.org>";
        Assert.False(SpamPolicy.Matches(rule, message));
        message.From = "othernews@example.com";
        Assert.False(SpamPolicy.Matches(rule, message));
        message.From = "news@example.com";
        message.Direction = MessageDirection.Outgoing;
        Assert.False(SpamPolicy.Matches(rule, message));
    }

    [Fact]
    public void DomainMatchIsExactAndValidationEnforcesIncomingGlobalPolicy()
    {
        var rule = new MailRule { IsSpamFilter = true, SpamBlockDomain = true, FromContains = "example.com",
            AccountId = Guid.NewGuid(), AlsoFilterOutMailbox = true, ApplyAutomatically = false,
            Action = RuleAction.Delete, UseBodyCondition = true };
        SpamPolicy.Validate(rule);
        Assert.Null(rule.AccountId);
        Assert.True(rule.ApplyAutomatically);
        Assert.False(rule.AlsoFilterOutMailbox);
        Assert.False(rule.UseBodyCondition);
        Assert.Equal(RuleAction.MoveToFolder, rule.Action);
        Assert.Equal("Junk", rule.TargetFolder);
        var message = Message();
        Assert.True(SpamPolicy.Matches(rule, message));
        message.From = "news@sub.example.com";
        Assert.False(SpamPolicy.Matches(rule, message));
        message.From = "news@example.com.evil.org";
        Assert.False(SpamPolicy.Matches(rule, message));
    }

    [Fact]
    public async Task FlagSurvivesRemoteUidChangeAndIsIndependentOfFolderAndFlags()
    {
        var store = Store();
        var message = Message();
        await store.UpsertSummariesAsync([message]);
        await store.SetSpamAsync(message, true);
        await store.DeleteSummariesAsync(message.AccountId, "In", ["1"]);
        message.MessageId = "991";
        message.FolderName = "Junk";
        await store.UpsertSummariesAsync([message, Message("2", "Junk")]);
        var result = await store.SearchLocalMessagesAsync(new LocalSearchQuery("is:spam"));
        var spam = Assert.Single(result.Messages);
        Assert.Equal("991", spam.MessageId);
        Assert.True(await store.IsSpamAsync(message));
        Assert.True(await store.IsSpamAsync(new MailMessageDetail {
            AccountId = message.AccountId, FolderName = message.FolderName, MessageId = message.MessageId }));
        await store.SetSpamAsync(message, false);
        Assert.Empty((await store.SearchLocalMessagesAsync(new LocalSearchQuery("is:spam"))).Messages);
        Assert.Equal(2, (await store.LoadFolderSummariesAsync(message.AccountId, "Junk")).Count);
    }

    [Fact]
    public async Task SpamClassificationDoesNotAlterUserFlags()
    {
        var store = Store();
        var message = Message();
        var flag = Guid.NewGuid().ToString();
        await store.UpsertSummariesAsync([message]);
        await store.UpdateFlagIdAsync(message.AccountId, message.FolderName, message.MessageId, flag);
        await store.SetSpamAsync(message, true);
        Assert.Equal(flag, Assert.Single(await store.LoadFolderSummariesAsync(message.AccountId, "In")).FlagId);
        await store.SetSpamAsync(message, false);
        Assert.Equal(flag, Assert.Single(await store.LoadFolderSummariesAsync(message.AccountId, "In")).FlagId);
    }

    [Fact]
    public async Task ReapplyingSpamInJunkMustNotDeleteTheMessage()
    {
        var store = Store();
        var message = Message(folder: "Junk");
        using var mail = new LocalMailService(store);
        await mail.ConnectAsync(new AccountModel { Id = message.AccountId, BackendKind = BackendKind.Pop3Smtp });
        await store.UpsertSummariesAsync([message]);
        var rules = new RuleService(mail, store, _directory);
        var rule = new MailRule { IsSpamFilter = true, FromContains = "news@example.com" };
        rules.SaveRules([rule]);
        await rules.ApplyRuleToMessagesAsync(rule, [message], store, TestContext.Current.CancellationToken);
        Assert.Single(await store.LoadFolderSummariesAsync(message.AccountId, "Junk"));
    }

    [Fact]
    public async Task RetentionStartsInJunkNotAtOriginalDateAndExcludesUnclassifiedMail()
    {
        var store = Store();
        var spam = Message(folder: "Junk");
        await store.UpsertSummariesAsync([spam, Message("2", "Junk")]);
        await store.SetSpamAsync(spam, true);
        Assert.Empty(await store.SpamIdsAsync(spam.AccountId, "Junk", 30));
        await using var connection = new SqliteConnection("Data Source=" + Path.Combine(_directory, "mail.db"));
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE SpamJunkAge SET first_seen_ticks=$time";
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.AddDays(-31).UtcTicks);
        await command.ExecuteNonQueryAsync();
        Assert.Equal(new[] { "1" }, await store.SpamIdsAsync(spam.AccountId, "Junk", 30));
        await store.SetSpamAsync(spam, false);
        Assert.Empty(await store.SpamIdsAsync(spam.AccountId, "Junk", 30));
    }

    [Fact]
    public async Task FirstSenderIsOfferedOnlyOnceAcrossStoreInstancesAndFolders()
    {
        var store = Store();
        var first = Message();
        await store.UpsertSummariesAsync([first]);
        Assert.True(await store.OfferSpamPromptAsync(first));
        Assert.False(await Store().OfferSpamPromptAsync(first));
        var known = Message("2", "Archive");
        known.From = "known@example.com";
        var next = Message("3"); next.From = known.From;
        await store.UpsertSummariesAsync([known, next]);
        Assert.False(await store.OfferSpamPromptAsync(next));
    }

    [Fact]
    public async Task SpamRuleRunsBeforeNormalRulesAndNeverProcessesOutgoing()
    {
        var store = Store();
        var message = Message();
        var account = new AccountModel { Id = message.AccountId, BackendKind = BackendKind.Pop3Smtp };
        using var mail = new LocalMailService(store);
        await mail.ConnectAsync(account);
        await store.UpsertSummariesAsync([message]);
        var rules = new RuleService(mail, store, _directory);
        rules.SaveRules([
            new MailRule { Name = "Normal", FromContains = "example.com", Action = RuleAction.Delete },
            new MailRule { Name = "SPAM", IsSpamFilter = true, FromContains = "news@example.com" }
        ]);
        var result = await rules.ApplyAutomaticRulesAsync([message], account.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.MatchedCount);
        Assert.Single(await store.LoadFolderSummariesAsync(account.Id, "Junk"));
        Assert.Empty(await store.LoadFolderSummariesAsync(account.Id, "Trash"));
        Assert.True(await store.IsSpamAsync(message));
        var outgoing = Message("2", "Sent"); outgoing.Direction = MessageDirection.Outgoing;
        Assert.Equal(0, (await rules.ApplyAutomaticRulesAsync([outgoing], account.Id, TestContext.Current.CancellationToken)).MatchedCount);
    }

    [Fact]
    public async Task LocalIdentityWithoutMessageIdFollowsMoveAndDoesNotReuseDeletedUid()
    {
        var store = Store();
        var message = Message();
        message.InternetMessageId = "";
        using var mail = new LocalMailService(store);
        await mail.ConnectAsync(new AccountModel { Id = message.AccountId, BackendKind = BackendKind.Pop3Smtp });
        await store.UpsertSummariesAsync([message]);
        await store.SetSpamAsync(message, true);
        await store.MoveLocalMessagesAsync(message.AccountId, "In", "Junk", ["1"]);
        message.FolderName = "Junk";
        Assert.True(await store.IsSpamAsync(message));
        await store.DeleteSummariesAsync(message.AccountId, "Junk", ["1"]);
        await store.UpsertSummariesAsync([message]);
        Assert.False(await store.IsSpamAsync(message));
    }

    [Fact]
    public async Task LeavingJunkPreservesSpamButResetsRetention()
    {
        var store = Store();
        var message = Message(folder: "Junk");
        using var mail = new LocalMailService(store);
        await mail.ConnectAsync(new AccountModel { Id = message.AccountId, BackendKind = BackendKind.Pop3Smtp });
        await store.UpsertSummariesAsync([message]);
        await store.SetSpamAsync(message, true);
        await store.SpamIdsAsync(message.AccountId, "Junk", 30);
        await using var connection = new SqliteConnection("Data Source=" + Path.Combine(_directory, "mail.db"));
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE SpamJunkAge SET first_seen_ticks=1";
        await command.ExecuteNonQueryAsync();
        await store.MoveLocalMessagesAsync(message.AccountId, "Junk", "In", ["1"]);
        Assert.Empty(await store.SpamIdsAsync(message.AccountId, "Junk", 30));
        Assert.Single((await store.SearchLocalMessagesAsync(new LocalSearchQuery("is:spam"))).Messages);
        await store.MoveLocalMessagesAsync(message.AccountId, "In", "Junk", ["1"]);
        Assert.Empty(await store.SpamIdsAsync(message.AccountId, "Junk", 30));
    }

    [Fact]
    public async Task ReusedRemoteUidWithDifferentInternetIdDoesNotInheritRetentionAge()
    {
        var store = Store();
        var message = Message(folder: "Junk");
        await store.UpsertSummariesAsync([message]);
        await store.SetSpamAsync(message, true);
        await store.SpamIdsAsync(message.AccountId, "Junk", 30);
        await using var connection = new SqliteConnection("Data Source=" + Path.Combine(_directory, "mail.db"));
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE SpamJunkAge SET first_seen_ticks=1";
        await command.ExecuteNonQueryAsync();
        message.InternetMessageId = "a-different-message@example.com";
        await store.UpsertSummariesAsync([message]);
        await store.SetSpamAsync(message, true);
        Assert.Empty(await store.SpamIdsAsync(message.AccountId, "Junk", 30));
    }

    [Fact]
    public void SpamRetentionRoundTripsAndCanBeDisabled()
    {
        var config = new ConfigService(new ProfileContext(_directory));
        var model = config.Load();
        Assert.Equal(30, model.SpamRetentionDays);
        model.SpamRetentionDays = 0;
        config.Save(model);
        Assert.Equal(0, config.Load().SpamRetentionDays);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
