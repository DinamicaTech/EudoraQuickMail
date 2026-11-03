using Microsoft.Data.Sqlite;

namespace QuickMail.Services;

public sealed record HomeMailCount(string Period, long Received, long Sent, long Unread);
public sealed record HomeFolderReview(Guid AccountId, string Folder, string DisplayName, long Count, bool Drafts);
public sealed record HomeMailStatistics(IReadOnlyDictionary<Guid, IReadOnlyList<HomeMailCount>> Accounts,
    IReadOnlyList<HomeFolderReview> Folders)
{
    public IReadOnlyList<HomeMailCount> ForAccount(Guid? account)
    {
        var names = new[] { "Today", "Yesterday", "This week", "This month", "All dates (pending)" };
        var rows = account.HasValue ? Accounts.Where(p => p.Key == account).Select(p => p.Value) : Accounts.Values;
        var lists = rows.ToList();
        return names.Select((name,i) => new HomeMailCount(name, lists.Sum(r => r[i].Received),
            lists.Sum(r => r[i].Sent), lists.Sum(r => r[i].Unread))).ToList();
    }
}

public partial class LocalStoreService
{
    /// <summary>Summary columns only; runs off the dispatcher. No bodies, files or network.</summary>
    public async Task<IReadOnlyList<HomeMailCount>> ReadHomeCountsAsync(DateTime today, Guid? account,
        CancellationToken ct = default) => (await ReadHomeStatisticsAsync(today, ct)).ForAccount(account);

    public Task<HomeMailStatistics> ReadHomeStatisticsAsync(DateTime today,
        CancellationToken ct = default) => Task.Run(() =>
    {
        using var timing = PerformanceLogService.Measure("Home: mail summary statistics", "all accounts, one pass");
        today = today.Date;
        var week = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var month = new DateTime(today.Year, today.Month, 1);
        var oldest = new[] { today.AddDays(-1), week, month }.Min();
        long Ticks(DateTime date) => new DateTimeOffset(date).UtcTicks;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(_connectionString)
            { Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var interrupt = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        ct.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        // UNION lets SQLite use the date and unread indexes independently. A Message-ID is
        // scoped to its account: copies under Gmail labels must not inflate the totals.
        command.CommandText = """
            WITH candidates AS (
                SELECT unique_id,account_id,folder_name,date_ticks,message_direction,is_read,internet_message_id
                FROM MessageSummary WHERE date_ticks >= $oldest AND date_ticks < $end
                UNION
                SELECT unique_id,account_id,folder_name,date_ticks,message_direction,is_read,internet_message_id
                FROM MessageSummary WHERE is_read=0
            ), messages AS (
                SELECT m.account_id,
                    CASE WHEN trim(m.internet_message_id)<>'' THEN 'mid:'||trim(m.internet_message_id)
                         ELSE 'local:'||m.folder_name||':'||m.unique_id END AS identity,
                    MAX(m.date_ticks) AS stamp, MAX(m.message_direction) AS direction,
                    MAX(m.is_read) AS read_state,
                    MAX(CASE WHEN COALESCE(f.kind,0) IN (4,5,12)
                        OR lower(m.folder_name) IN ('trash','junk','recoverydeleted','[gmail]/spam','[gmail]/trash')
                        THEN 1 ELSE 0 END) AS excluded
                FROM candidates m LEFT JOIN Folder f ON f.account_id=m.account_id AND f.full_name=m.folder_name
                WHERE COALESCE(f.kind,0) NOT IN (3,10)
                    AND lower(m.folder_name) NOT IN ('draft','drafts','scheduled')
                GROUP BY m.account_id, identity
            )
            SELECT stamp,direction,read_state,excluded,account_id FROM messages
            """;
        command.Parameters.AddWithValue("$oldest", Ticks(oldest));
        command.Parameters.AddWithValue("$end", Ticks(today.AddDays(1)));
        var periods = new[] { ("Today", today), ("Yesterday", today.AddDays(-1)), ("This week", week), ("This month", month) };
        var starts = periods.Select(p => Ticks(p.Item2)).ToArray();
        var ends = periods.Select((p,i) => Ticks(i == 1 ? today : today.AddDays(1))).ToArray();
        var totals = new Dictionary<Guid, long[,]>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var date = reader.GetInt64(0); var direction = reader.GetInt32(1);
            var pending = reader.GetInt32(2) == 0 && reader.GetInt32(3) == 0;
            if (!Guid.TryParse(reader.GetString(4), out var owner)) continue;
            if (!totals.TryGetValue(owner, out var counts)) totals[owner] = counts = new long[5,3];
            if (pending) counts[4,2]++;
            for (var i = 0; i < periods.Length; i++)
            {
                if (date < starts[i] || date >= ends[i]) continue;
                if (direction == 1) counts[i,0]++;
                if (direction == 2) counts[i,1]++;
                if (pending) counts[i,2]++;
            }
        }
        reader.Close();
        command.Parameters.Clear();
        command.CommandText = """
            SELECT m.account_id,m.folder_name,COALESCE(f.display_name,m.folder_name),COUNT(*),
                   CASE WHEN COALESCE(f.kind,0)=3 OR lower(m.folder_name) IN ('draft','drafts') THEN 1 ELSE 0 END AS draft
            FROM MessageSummary m LEFT JOIN Folder f ON f.account_id=m.account_id AND f.full_name=m.folder_name
            WHERE (m.is_read=0 OR COALESCE(f.kind,0)=3 OR lower(m.folder_name) IN ('draft','drafts'))
                AND COALESCE(f.kind,0) NOT IN (6,7,8,10,12)
                AND lower(m.folder_name) NOT IN ('scheduled','recoverydeleted','[gmail]/all mail','[gmail]/todos','[gmail]/important','[gmail]/starred')
            GROUP BY m.account_id,m.folder_name ORDER BY COUNT(*) DESC LIMIT 8
            """;
        var folders = new List<HomeFolderReview>();
        using var folderReader = command.ExecuteReader();
        while (folderReader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (Guid.TryParse(folderReader.GetString(0), out var owner))
                folders.Add(new(owner,folderReader.GetString(1),folderReader.GetString(2),folderReader.GetInt64(3),folderReader.GetInt32(4)==1));
        }
        return new HomeMailStatistics(totals.ToDictionary(p => p.Key, p => (IReadOnlyList<HomeMailCount>)
            periods.Select((period,i) => new HomeMailCount(period.Item1,p.Value[i,0],p.Value[i,1],p.Value[i,2]))
                .Append(new HomeMailCount("All dates (pending)",0,0,p.Value[4,2])).ToList()),folders);
    }, ct);
}
