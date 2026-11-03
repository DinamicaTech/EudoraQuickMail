using System.IO;
using Microsoft.Data.Sqlite;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class HomeDashboardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EQM-home-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void IdleDefersDuringWorkAndOnlyFiresOncePerEpisode()
    {
        var idle = new HomeIdlePolicy();
        Assert.False(idle.ShouldShow(TimeSpan.FromMinutes(14),true,false));
        Assert.False(idle.ShouldShow(TimeSpan.FromMinutes(16),true,true));
        Assert.True(idle.ShouldShow(TimeSpan.FromMinutes(17),true,false));
        Assert.False(idle.ShouldShow(TimeSpan.FromMinutes(40),true,false));
        idle.Input(TimeSpan.FromMinutes(40));
        Assert.False(idle.ShouldShow(TimeSpan.FromMinutes(54),true,false));
        Assert.True(idle.ShouldShow(TimeSpan.FromMinutes(55),true,false));
        Assert.False(idle.ShouldShow(TimeSpan.FromMinutes(80),false,false));
        Assert.False(idle.ShouldShow(TimeSpan.FromMinutes(81),true,false));
    }
    [Fact]
    public void HealthSurvivesRestartAndSuccessOnlyClearsItsOwnChannel()
    {
        Directory.CreateDirectory(_directory);
        var account = Guid.NewGuid();
        var journal = new AccountHealthJournal(_directory);
        journal.Record(account,"Send",new IOException("private OAuth response"));
        journal.Record(account,"Receive");
        journal = new AccountHealthJournal(_directory);
        Assert.NotNull(journal.Read().Single(r => r.Channel == "Send").Error);
        Assert.Null(journal.Read().Single(r => r.Channel == "Receive").Error);
        Assert.DoesNotContain("private OAuth",File.ReadAllText(Path.Combine(_directory,"account-health.json")));
        journal.Record(account,"Send",new OperationCanceledException());
        Assert.NotNull(journal.Read().Single(r => r.Channel == "Send").Error);
        journal.Record(account,"Send");
        Assert.Null(journal.Read().Single(r => r.Channel == "Send").Error);
    }
    [Fact]
    public void InactivityOptionPersistsAndDefaultsOn()
    {
        Directory.CreateDirectory(_directory);
        var profile = new ProfileContext(_directory);
        var service = new ConfigService(profile);
        var config = service.Load();
        Assert.True(config.ShowHomeAfterInactivity);
        config.ShowHomeAfterInactivity = false;
        service.Save(config);
        Assert.False(new ConfigService(profile).Load().ShowHomeAfterInactivity);
    }

    [Fact]
    public void CalendarFailureKeepsPreviousSuccessAndIsIndependentOfMailHealth()
    {
        Directory.CreateDirectory(_directory);
        var account=Guid.NewGuid();var journal=new AccountHealthJournal(_directory);
        journal.Record(account,"Calendar");
        var success=journal.Read().Single().SuccessUtc;
        journal.Record(account,"Calendar",new IOException("sensitive provider payload"));
        journal.Record(account,"Send");journal.Record(account,"Receive");
        var calendar=new AccountHealthJournal(_directory).Read().Single(r=>r.Channel=="Calendar");
        Assert.Equal(success,calendar.SuccessUtc);Assert.NotNull(calendar.Error);
        Assert.Contains("calendar account settings",calendar.Error);
        Assert.DoesNotContain("sensitive provider payload",File.ReadAllText(Path.Combine(_directory,"account-health.json")));
        journal.Record(account,"Calendar",new OperationCanceledException());
        Assert.NotNull(journal.Read().Single(r=>r.Channel=="Calendar").Error);
        journal.Record(account,"Calendar");
        Assert.Null(journal.Read().Single(r=>r.Channel=="Calendar").Error);
    }
    [Fact]
    public async Task CountsDeduplicateLabelsRespectDatesAccountsAndPendingExclusions()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory)); store.Initialize();
        var account = Guid.NewGuid(); var other = Guid.NewGuid();
        var today = new DateTime(2026,10,18);
        MailMessageSummary Row(string id,string folder,DateTime day,MessageDirection direction=MessageDirection.Incoming,Guid? owner=null) => new()
        {
            MessageId=id, AccountId=owner??account,FolderName=folder,InternetMessageId=$"<{id}@test>",
            Date=new DateTimeOffset(day.AddHours(10)),Direction=direction
        };
        await store.UpsertSummariesAsync([
            Row("a","INBOX",today),Row("a","[Gmail]/All Mail",today),
            Row("a","INBOX",today,owner:other),Row("b","Out",today,MessageDirection.Outgoing),
            Row("c","RecoveryDeleted",today),Row("d","Scheduled",today,MessageDirection.Outgoing),
            Row("e","In",today.AddDays(-1)),Row("old","In",today.AddYears(-1))]);
        var counts = await store.ReadHomeCountsAsync(today,account);
        Assert.Equal(new HomeMailCount("Today",2,1,2),counts[0]);
        Assert.Equal(1,counts[1].Received);
        Assert.Equal(4,counts[^1].Unread);
        var all = await store.ReadHomeCountsAsync(today,null);
        Assert.Equal(3,all[0].Received);
        using var cancelled = new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadHomeCountsAsync(today,null,cancelled.Token));
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory,true);
    }

    [Fact]
    public async Task SummaryQueryHandlesOneHundredThousandRowsWithoutLoadingBodies()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory)); store.Initialize();
        var account = Guid.NewGuid();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory,"mail.db")}"))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<100000)
                INSERT INTO MessageSummary(unique_id,account_id,folder_name,date_ticks,message_direction,is_read,internet_message_id)
                SELECT cast(n AS TEXT),$account,'In',$date,1,0,'<'||n||'@test>' FROM numbers;
                """;
            insert.Parameters.AddWithValue("$account",account.ToString());
            insert.Parameters.AddWithValue("$date",DateTimeOffset.Now.UtcTicks);
            insert.ExecuteNonQuery();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var rows = await store.ReadHomeCountsAsync(DateTime.Today,null,timeout.Token);
        Assert.Equal(100000,rows[0].Received);
        Assert.Equal(100000,rows[^1].Unread);
    }
}
