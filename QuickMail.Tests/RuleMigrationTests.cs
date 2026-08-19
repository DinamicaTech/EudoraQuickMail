using System;
using System.Collections.Generic;
using System.IO;
using QuickMail.Models;
using QuickMail.Services;
using Xunit;

namespace QuickMail.Tests;

/// <summary>Repairs obsolete per-account expansion without broadening genuine account rules.</summary>
public class RuleMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"qm-rule-repair-{Guid.NewGuid():N}");

    public RuleMigrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private sealed class FixedAccountService(params AccountModel[] accounts) : IAccountService
    {
        public List<AccountModel> LoadAccounts() => [.. accounts];
        public void SaveAccounts(List<AccountModel> value) { }
        public void SetDefaultAccount(Guid accountId) { }
    }

    private static AccountModel Imap(string name) => new()
        { Id = Guid.NewGuid(), AccountName = name, BackendKind = BackendKind.ImapSmtp };
    private static AccountModel Graph(string name) => new()
        { Id = Guid.NewGuid(), AccountName = name, BackendKind = BackendKind.MicrosoftGraph };

    private void Seed(params MailRule[] rules) =>
        new RuleService(new StubImapMailService(), new StubLocalStoreService(), _dir).SaveRules([.. rules]);

    private RuleService Service(params AccountModel[] accounts) =>
        new(new StubImapMailService(), new StubLocalStoreService(), _dir, new FixedAccountService(accounts));

    private static MailRule Copy(string name, Guid accountId, string subject = "x") => new()
    {
        Name = name, AccountId = accountId, SubjectContains = subject,
        UseSubjectCondition = true, Action = RuleAction.MoveToFolder, TargetFolder = "Archive"
    };

    [Fact]
    public void IdenticalCopiesCoveringEveryImapAccount_BecomeOneGlobalRule()
    {
        var a = Imap("A"); var b = Imap("B");
        Seed(Copy("Newsletters", a.Id), Copy("Newsletters", b.Id));

        var rule = Assert.Single(Service(a, b).LoadRules());

        Assert.Null(rule.AccountId);
        Assert.Equal("Newsletters", rule.Name);
    }

    [Fact]
    public void AccountSpecificRule_IsNotBroadened()
    {
        var a = Imap("A"); var b = Imap("B");
        Seed(Copy("Only A", a.Id));

        var rule = Assert.Single(Service(a, b).LoadRules());

        Assert.Equal(a.Id, rule.AccountId);
    }

    [Fact]
    public void SimilarButDifferentRules_AreNotConsolidated()
    {
        var a = Imap("A"); var b = Imap("B");
        Seed(Copy("Rule", a.Id, "one"), Copy("Rule", b.Id, "two"));

        Assert.Equal(2, Service(a, b).LoadRules().Count);
    }

    [Fact]
    public void GraphAccount_DoesNotPreventRepairOfImapCopies()
    {
        var a = Imap("A"); var b = Imap("B"); var graph = Graph("Work");
        Seed(Copy("Global", a.Id), Copy("Global", b.Id));

        var rule = Assert.Single(Service(a, b, graph).LoadRules());

        Assert.Null(rule.AccountId);
    }

    [Fact]
    public void ExistingGlobalRule_RemainsGlobal()
    {
        var a = Imap("A"); var b = Imap("B");
        Seed(new MailRule { Name = "Global", AccountId = null });

        var rule = Assert.Single(Service(a, b).LoadRules());

        Assert.Null(rule.AccountId);
    }

    [Fact]
    public void Repair_IsPersistedAndIdempotent()
    {
        var a = Imap("A"); var b = Imap("B");
        Seed(Copy("Global", a.Id), Copy("Global", b.Id));

        Assert.Single(Service(a, b).LoadRules());
        var second = Service(a, b).LoadRules();

        Assert.Single(second);
        Assert.Null(second[0].AccountId);
    }
}
