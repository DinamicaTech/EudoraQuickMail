using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class StabilizationRegressionTests : IDisposable
{
    private readonly ProfileContext _profile = new(Path.Combine(Path.GetTempPath(), "qm-stability-" + Guid.NewGuid().ToString("N")));
    private readonly LocalStoreService _store;
    private readonly AccountService _accounts;
    private readonly AccountModel _account = new() { Id = Guid.NewGuid(), Username = "test@example.invalid", IsActive = true,
        BackendKind = BackendKind.Pop3Smtp, FolderTreeRootId = Guid.NewGuid(), FolderTreeRootName = "Test mail" };
    public StabilizationRegressionTests()
    {
        _store = new(_profile); _store.Initialize();
        _accounts = new(_profile); _accounts.SaveAccounts([_account]);
    }
    private ScheduledMail Item(bool accepted = false) => new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1),
        new ComposeModel { AccountId = _account.Id, To = "recipient@example.invalid", Subject = "test", Body = "body" },
        LocalMessageId: "scheduled-" + Guid.NewGuid().ToString("N"), TransportAccepted: accepted);
    private async Task Seed(params ScheduledMail[] items)
    {
        await _store.EnsureLocalSystemFoldersAsync([_account]);
        foreach (var item in items)
            await _store.SaveLocalMessageAsync(new() { AccountId = _account.Id, MessageId = item.LocalMessageId!, FolderName = "Scheduled", Subject = "test", Date = item.SendAtUtc });
        await File.WriteAllTextAsync(Path.Combine(_profile.ProfileDir, "scheduled-mail.json"), JsonSerializer.Serialize(items));
    }
    private ScheduledSendService Queue(ISendMailService sender, IMailService? mail = null, MailActivityPolicyService? activity = null) =>
        new(_profile, sender, _accounts, new StubCredentialService(), _store, mail ?? new LocalMailService(_store, _accounts), activity);

    [Fact]
    public async Task FailedArchivalSurvivesRestartAndNeverRepeatsSmtp()
    {
        var item = Item(); await Seed(item);
        var sends = 0;
        var sender = FaultProxy.Create<ISendMailService>((_, _) => { sends++; return Task.CompletedTask; });
        var broken = FaultProxy.Create<IMailService>((_, _) => Task.FromException(new IOException("Sent unavailable")));
        using (var queue = Queue(sender, broken))
        {
            Assert.Single(await queue.DispatchDueAsync());
            var pending = Assert.Single(await queue.GetSnapshotAsync());
            Assert.True(pending.TransportAccepted);
            Assert.False(pending.SentCopySaved);
            Assert.NotNull(pending.NextAttemptUtc);
            Assert.Single(await _store.LoadFolderSummariesAsync(_account.Id, "Scheduled"));
            Assert.Empty(await queue.DispatchDueAsync()); // respect retry backoff
        }
        using (var restarted = Queue(sender))
        {
            Assert.Empty(await restarted.DispatchDueAsync(manual: true));
            Assert.Empty(await restarted.GetSnapshotAsync());
            Assert.Single(await _store.LoadFolderSummariesAsync(_account.Id, "Sent"));
            Assert.Empty(await _store.LoadFolderSummariesAsync(_account.Id, "Scheduled"));
        }
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task OfflineDuringBatchFinishesCurrentMessageButLeavesNextQueued()
    {
        await Seed(Item(), Item());
        using var activity = new MailActivityPolicyService(_profile);
        var sends = 0;
        var sender = FaultProxy.Create<ISendMailService>((_, _) => { sends++; activity.SetOffline(); return Task.CompletedTask; });
        using var queue = Queue(sender, activity: activity);
        Assert.Empty(await queue.DispatchDueAsync());
        Assert.Equal(1, sends);
        Assert.Single(await queue.GetSnapshotAsync());
        Assert.Empty(await queue.DispatchDueAsync(manual: true));
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task PermanentArchivalFailureWaitsForManualRetryWithoutSendingAgain()
    {
        await Seed(Item());
        var sender = new StubSmtpService();
        var calls = 0;
        var broken = FaultProxy.Create<IMailService>((_, _) => { calls++; return Task.FromException(new UnauthorizedAccessException("Access denied")); });
        using var queue = Queue(sender, broken);
        Assert.Single(await queue.DispatchDueAsync());
        Assert.Empty(await queue.DispatchDueAsync());
        Assert.Equal(1, calls);
        Assert.Single(await queue.DispatchDueAsync(manual: true));
        Assert.Equal(2, calls);
        Assert.Single(sender.Sent);
        Assert.True(Assert.Single(await queue.GetSnapshotAsync()).TransportAccepted);
    }

    [Fact]
    public async Task AcceptedItemsCannotBeEditedOrReplacedOrUndone()
    {
        var item = Item(accepted: true) with { NextAttemptUtc = DateTimeOffset.UtcNow.AddDays(1) };
        await Seed(item);
        using var queue = Queue(new StubSmtpService());
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ReplaceAsync(item.Id, item.Message, DateTimeOffset.UtcNow.AddDays(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.QueueImmediateAsync(item.Message, item.Id));
        Assert.Null(await queue.UndoSendAsync(item.Id));
        Assert.True(Assert.Single(await queue.GetSnapshotAsync()).TransportAccepted);
    }

    [Fact]
    public async Task SavedArchivalCheckpointDoesNotAppendAgainAfterRestart()
    {
        await Seed(Item(accepted: true) with { SentCopySaved = true });
        var calls = 0;
        var mail = FaultProxy.Create<IMailService>((_, _) => { calls++; throw new Exception("Must not append again"); });
        var sender = new StubSmtpService();
        using var queue = Queue(sender, mail);
        Assert.Empty(await queue.DispatchDueAsync());
        Assert.Empty(await queue.GetSnapshotAsync());
        Assert.Equal(0, calls);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task CleanupFailureAfterArchivalRetriesOnlyCleanup()
    {
        await Seed(Item());
        async Task Sql(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={Path.Combine(_profile.ProfileDir, "mail.db")}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        await Sql("CREATE TRIGGER reject_cleanup BEFORE DELETE ON MessageSummary WHEN OLD.folder_name='Scheduled' BEGIN SELECT RAISE(ABORT,'synthetic cleanup failure'); END;");
        var sender = new StubSmtpService();
        using (var queue = Queue(sender))
        {
            Assert.Single(await queue.DispatchDueAsync());
            Assert.True(Assert.Single(await queue.GetSnapshotAsync()).SentCopySaved);
            Assert.Single(await _store.LoadFolderSummariesAsync(_account.Id, "Sent"));
        }
        await Sql("DROP TRIGGER reject_cleanup;");
        var unexpectedAppend = FaultProxy.Create<IMailService>((_, _) => throw new Exception("Must not append twice"));
        using (var queue = Queue(sender, unexpectedAppend))
        {
            Assert.Empty(await queue.DispatchDueAsync(manual: true));
            Assert.Empty(await queue.GetSnapshotAsync());
        }
        Assert.Single(sender.Sent);
        Assert.Empty(await _store.LoadFolderSummariesAsync(_account.Id, "Scheduled"));
        Assert.Single(await _store.LoadFolderSummariesAsync(_account.Id, "Sent"));
    }

    [Fact]
    public async Task WaitingDispatcherRechecksOfflineAfterAcquiringGate()
    {
        await Seed(Item(), Item());
        using var activity = new MailActivityPolicyService(_profile);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var sender = FaultProxy.Create<ISendMailService>((_, _) => { sends++; started.TrySetResult(); return release.Task; });
        using var queue = Queue(sender, activity: activity);
        var first = queue.DispatchDueAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = queue.DispatchDueAsync();
        activity.SetOffline(); release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, sends);
        Assert.Single(await queue.GetSnapshotAsync());
    }

    [Theory]
    [InlineData("accounts.json")]
    [InlineData("rules.json")]
    [InlineData("views.json")]
    public void CorruptConfigurationCannotBeMistakenForEmptyOrOverwritten(string name)
    {
        var path = Path.Combine(_profile.ProfileDir, name);
        File.WriteAllText(path, "{corrupt");
        var rules = new RuleService(new StubImapMailService(), _store, _profile.ProfileDir);
        var views = new ViewService(_profile);
        Action load = name switch { "accounts.json" => () => _accounts.LoadAccounts(), "rules.json" => () => rules.LoadRules(), _ => () => views.Load() };
        Action save = name switch { "accounts.json" => () => _accounts.SaveAccounts([]), "rules.json" => () => rules.SaveRules([]), _ => () => views.Save([]) };
        Assert.Throws<IOException>(load);
        Assert.Throws<IOException>(save);
        Assert.Equal("{corrupt", File.ReadAllText(path));
        Assert.Throws<IOException>(load); // failure must not cache an empty result
    }

    [Fact]
    public void PreviousValidConfigurationIsAvailableForRecovery()
    {
        _accounts.SaveAccounts([]);
        var previous = File.ReadAllText(Path.Combine(_profile.ProfileDir, "accounts.json.previous"));
        Assert.Equal(_account.Id, Assert.Single(JsonSerializer.Deserialize<List<AccountModel>>(previous)!).Id);
    }

    [Fact]
    public void LockedConfigurationCannotBeOverwrittenAndRecoversAfterUnlock()
    {
        var path = Path.Combine(_profile.ProfileDir, "accounts.json");
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => _accounts.LoadAccounts());
            Assert.Throws<IOException>(() => _accounts.SaveAccounts([]));
        }
        Assert.Equal(_account.Id, Assert.Single(_accounts.LoadAccounts()).Id);
    }

    [Fact]
    public async Task AdvancedOrAndDateCriteriaUseIndexAndRespectScopes()
    {
        await _store.SaveLocalMessageAsync(new() { AccountId = _account.Id, MessageId = "one", FolderName = "In", Subject = "Mitsubishi", PlainTextBody = "hello", Date = DateTimeOffset.Now });
        await _store.SaveLocalMessageAsync(new() { AccountId = _account.Id, MessageId = "two", FolderName = "Other", Subject = "other", PlainTextBody = "hello", Date = DateTimeOffset.Now });
        AdvancedSearchCriterion[] criteria = [new("Subject", "contains", "Mitsubishi"), new("Body", "contains", "hello", "OR")];
        Assert.Equal(2, (await _store.SearchLocalMessagesAdvancedAsync(new(criteria))).TotalMatches);
        Assert.Single((await _store.SearchLocalMessagesAdvancedAsync(new(criteria, FolderName: "In"))).Messages);
        Assert.Equal(2, (await _store.SearchLocalMessagesAdvancedAsync(new([new("Date", "=", DateTime.Now.ToShortDateString())]))).TotalMatches);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.SearchLocalMessagesAdvancedAsync(new(criteria), cancelled.Token));
        Assert.Equal(2, (await _store.SearchLocalMessagesAdvancedAsync(new(criteria))).TotalMatches);
    }

    [Fact]
    public async Task IndexedSearchAtVolumeReturnsStablePagesWithinBudget()
    {
        // Bulk synthetic fixture: 100,000 independent message identities, no production data.
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_profile.ProfileDir, "mail.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                BEGIN;
                WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<100000)
                INSERT INTO MessageSummary(unique_id,account_id,folder_name,subject,date_ticks)
                SELECT CAST(n AS TEXT),$account,'In',CASE WHEN n%1000=0 THEN 'Mitsubishi' ELSE 'ordinary newsletter' END,$ticks+n FROM numbers;
                INSERT INTO LocalMessageFts(account_id,unique_id,folder_name,subject,body_text)
                SELECT account_id,unique_id,folder_name,subject,'synthetic body' FROM MessageSummary;
                INSERT INTO LocalMessageFtsKey(account_id,unique_id,folder_name,fts_rowid)
                SELECT account_id,unique_id,folder_name,rowid FROM LocalMessageFts;
                COMMIT;
                """;
            command.Parameters.AddWithValue("$account", _account.Id.ToString());
            command.Parameters.AddWithValue("$ticks", DateTimeOffset.UtcNow.UtcTicks);
            await command.ExecuteNonQueryAsync();
        }
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var quick = await Task.Run(() => _store.SearchLocalMessagesAsync(new("Mitsubishi", Limit: 10), budget.Token));
        var advanced = await Task.Run(() => _store.SearchLocalMessagesAdvancedAsync(new(
            [new("Subject", "contains", "Mitsubishi"), new("Body", "contains", "nonexistent", "OR")], Limit: 10), budget.Token));
        Assert.Equal(100, quick.TotalMatches);
        Assert.Equal(100, advanced.TotalMatches);
        Assert.Equal(quick.Messages.Select(x => x.MessageId), advanced.Messages.Select(x => x.MessageId));
        var page2 = await Task.Run(() => _store.SearchLocalMessagesAdvancedAsync(new(
            [new("Subject", "contains", "Mitsubishi")], Limit: 10, Offset: 10), budget.Token));
        Assert.Empty(advanced.Messages.Select(x => x.MessageId).Intersect(page2.Messages.Select(x => x.MessageId)));
        Assert.Equal(10, page2.Messages.Count);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_profile.ProfileDir, true);
    }

    public class FaultProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args);
        public static T Create<T>(Func<string, object?[]?, object?> handler) where T : class
        { var proxy = Create<T, FaultProxy>(); ((FaultProxy)(object)proxy).Handler = handler; return proxy; }
    }
}
