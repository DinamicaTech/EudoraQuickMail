using QuickMail.Models;

namespace QuickMail.Services;

public partial class LocalStoreService
{
    public async Task<List<MailMessageDetail>> GetFollowUpCandidatesAsync(Guid accountId, FollowUpTracking tracking, CancellationToken ct)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.unique_id,s.folder_name,s.from_disp,s.date_ticks,COALESCE(d.raw_headers,'')
            FROM MessageSummary s LEFT JOIN MessageDetail d USING(unique_id,account_id,folder_name)
            WHERE s.account_id=$account AND s.date_ticks >= $since AND s.message_direction <> 2
            ORDER BY s.date_ticks DESC;
            """;
        command.Parameters.AddWithValue("$account", accountId.ToString());
        command.Parameters.AddWithValue("$since", tracking.SentAtUtc.UtcTicks);
        var recipients = FollowUpReminder.Addresses(tracking.Recipients);
        var result = new List<MailMessageDetail>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var from = reader.GetString(2);
            if (!FollowUpReminder.Addresses(from).Overlaps(recipients)) continue;
            result.Add(new MailMessageDetail { AccountId = accountId, MessageId = reader.GetString(0),
                FolderName = reader.GetString(1), From = from, Date = new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero),
                RawHeaders = reader.GetString(4) });
        }
        return result;
    }
}
