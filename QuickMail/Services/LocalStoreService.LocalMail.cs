using Microsoft.Data.Sqlite;
using QuickMail.Models;

namespace QuickMail.Services;

public partial class LocalStoreService
{
    public async Task<bool> ContainsPop3UidAsync(Guid accountId, string uidl, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM Pop3Receipt WHERE account_id=$aid AND uidl=$uidl);";
        cmd.Parameters.AddWithValue("$aid", accountId.ToString());
        cmd.Parameters.AddWithValue("$uidl", uidl);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0) != 0;
    }

    public Task SavePop3MessageAsync(string uidl, MailMessageDetail message, CancellationToken ct = default) =>
        SaveAuthoritativeMessageAsync(message, uidl, ct);

    public Task SaveLocalMessageAsync(MailMessageDetail message, CancellationToken ct = default) =>
        SaveAuthoritativeMessageAsync(message, null, ct);

    private async Task SaveAuthoritativeMessageAsync(MailMessageDetail message, string? uidl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId)) throw new ArgumentException("MessageId is required.");
        if (string.IsNullOrWhiteSpace(message.FolderName)) throw new ArgumentException("FolderName is required.");
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await EnsureNotContainerAsync(conn, tx, message.AccountId, message.FolderName, allowMissing: true, ct);

        await using (var summary = conn.CreateCommand())
        {
            summary.Transaction = tx;
            summary.CommandText = """
                INSERT INTO MessageSummary
                    (unique_id,account_id,folder_name,from_disp,to_addr,subject,date_ticks,is_read,
                     preview_text,is_replied,is_forwarded,has_attachments,is_mailing_list,flag_id,internet_message_id)
                VALUES ($uid,$aid,$fn,$from,$to,$subject,$date,$read,$preview,$replied,$forwarded,0,$list,NULL,$imid)
                ON CONFLICT(unique_id,account_id,folder_name) DO UPDATE SET
                    from_disp=excluded.from_disp,to_addr=excluded.to_addr,subject=excluded.subject,
                    date_ticks=excluded.date_ticks,is_read=excluded.is_read,preview_text=excluded.preview_text,
                    internet_message_id=excluded.internet_message_id;
                """;
            AddMessageKey(summary, message);
            summary.Parameters.AddWithValue("$from", message.From ?? string.Empty);
            summary.Parameters.AddWithValue("$to", message.To ?? string.Empty);
            summary.Parameters.AddWithValue("$subject", message.Subject ?? string.Empty);
            summary.Parameters.AddWithValue("$date", message.Date.UtcTicks);
            summary.Parameters.AddWithValue("$read", message.IsRead ? 1 : 0);
            summary.Parameters.AddWithValue("$preview", message.Preview ?? string.Empty);
            summary.Parameters.AddWithValue("$replied", message.IsReplied ? 1 : 0);
            summary.Parameters.AddWithValue("$forwarded", message.IsForwarded ? 1 : 0);
            summary.Parameters.AddWithValue("$list", message.IsMailingList ? 1 : 0);
            summary.Parameters.AddWithValue("$imid", message.InternetMessageId ?? string.Empty);
            await summary.ExecuteNonQueryAsync(ct);
        }

        await using (var detail = conn.CreateCommand())
        {
            detail.Transaction = tx;
            detail.CommandText = """
                INSERT INTO MessageDetail
                    (unique_id,account_id,folder_name,to_addr,cc,reply_to,plain_body,html_body,attachments_json,calendar_ics,raw_headers)
                VALUES ($uid,$aid,$fn,$to,$cc,$reply,$plain,$html,NULL,NULL,$headers)
                ON CONFLICT(unique_id,account_id,folder_name) DO UPDATE SET
                    to_addr=excluded.to_addr,cc=excluded.cc,reply_to=excluded.reply_to,
                    plain_body=excluded.plain_body,html_body=excluded.html_body,
                    attachments_json=NULL,calendar_ics=NULL,raw_headers=excluded.raw_headers;
                """;
            AddMessageKey(detail, message);
            detail.Parameters.AddWithValue("$to", message.To ?? string.Empty);
            detail.Parameters.AddWithValue("$cc", message.Cc ?? string.Empty);
            detail.Parameters.AddWithValue("$reply", message.ReplyTo ?? string.Empty);
            detail.Parameters.AddWithValue("$plain", message.PlainTextBody ?? string.Empty);
            detail.Parameters.AddWithValue("$html", message.HtmlBody ?? string.Empty);
            detail.Parameters.AddWithValue("$headers", message.RawHeaders ?? string.Empty);
            await detail.ExecuteNonQueryAsync(ct);
        }

        await using (var fts = conn.CreateCommand())
        {
            fts.Transaction = tx;
            fts.CommandText = """
                DELETE FROM LocalMessageFts
                 WHERE rowid IN (SELECT fts_rowid FROM LocalMessageFtsKey
                                  WHERE account_id=$aid AND unique_id=$uid AND folder_name=$fn);
                DELETE FROM LocalMessageFtsKey WHERE account_id=$aid AND unique_id=$uid AND folder_name=$fn;
                INSERT INTO LocalMessageFts
                    (account_id,unique_id,folder_name,from_addr,to_addr,cc_addr,subject,body_text)
                VALUES ($aid,$uid,$fn,$from,$to,$cc,$subject,$body);
                INSERT INTO LocalMessageFtsKey(account_id,unique_id,folder_name,fts_rowid)
                VALUES ($aid,$uid,$fn,last_insert_rowid());
                """;
            AddMessageKey(fts, message);
            fts.Parameters.AddWithValue("$from", message.From ?? string.Empty);
            fts.Parameters.AddWithValue("$to", message.To ?? string.Empty);
            fts.Parameters.AddWithValue("$cc", message.Cc ?? string.Empty);
            fts.Parameters.AddWithValue("$subject", message.Subject ?? string.Empty);
            fts.Parameters.AddWithValue("$body", string.IsNullOrWhiteSpace(message.PlainTextBody)
                ? message.HtmlBody ?? string.Empty : message.PlainTextBody);
            await fts.ExecuteNonQueryAsync(ct);
        }

        if (uidl is not null)
        {
            await using var receipt = conn.CreateCommand();
            receipt.Transaction = tx;
            receipt.CommandText = """
                INSERT INTO Pop3Receipt(account_id,uidl,unique_id,received_utc)
                VALUES($aid,$uidl,$uid,$now) ON CONFLICT(account_id,uidl) DO NOTHING;
                """;
            receipt.Parameters.AddWithValue("$aid", message.AccountId.ToString());
            receipt.Parameters.AddWithValue("$uidl", uidl);
            receipt.Parameters.AddWithValue("$uid", message.MessageId);
            receipt.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
            await receipt.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static void AddMessageKey(SqliteCommand command, MailMessageDetail message)
    {
        command.Parameters.AddWithValue("$uid", message.MessageId);
        command.Parameters.AddWithValue("$aid", message.AccountId.ToString());
        command.Parameters.AddWithValue("$fn", message.FolderName);
    }

    public async Task<LocalSearchResult> SearchLocalMessagesAsync(LocalSearchQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Text)) return new LocalSearchResult([], 0);
        var limit = Math.Clamp(query.Limit, 1, LocalMailConstants.MaxRenderedMessages);
        var accountFilter = query.AccountId.HasValue ? " AND f.account_id=$aid" : string.Empty;
        var folderFilter = !string.IsNullOrWhiteSpace(query.FolderName)
            ? query.IncludeDescendants ? " AND (f.folder_name=$fn OR (f.folder_name >= $ds AND f.folder_name < $de))" : " AND f.folder_name=$fn"
            : string.Empty;
        var order = query.Sort switch
        {
            LocalSearchSort.OldestFirst => "s.date_ticks ASC",
            LocalSearchSort.Relevance => "bm25(LocalMessageFts)",
            LocalSearchSort.FromAscending => "s.from_disp COLLATE NOCASE ASC",
            LocalSearchSort.FromDescending => "s.from_disp COLLATE NOCASE DESC",
            LocalSearchSort.ToAscending => "s.to_addr COLLATE NOCASE ASC",
            LocalSearchSort.ToDescending => "s.to_addr COLLATE NOCASE DESC",
            LocalSearchSort.SubjectAscending => "s.subject COLLATE NOCASE ASC",
            LocalSearchSort.SubjectDescending => "s.subject COLLATE NOCASE DESC",
            LocalSearchSort.ReadAscending => "s.is_read ASC, s.date_ticks DESC",
            LocalSearchSort.ReadDescending => "s.is_read DESC, s.date_ticks DESC",
            LocalSearchSort.AttachmentsFirst => "s.has_attachments DESC, s.date_ticks DESC",
            LocalSearchSort.AttachmentsLast => "s.has_attachments ASC, s.date_ticks DESC",
            _ => "s.date_ticks DESC",
        };
        await using var conn = await OpenAsync();
        long total;
        await using (var count = conn.CreateCommand())
        {
            count.CommandText = $"SELECT count(*) FROM LocalMessageFts f WHERE LocalMessageFts MATCH $q{accountFilter}{folderFilter};";
            AddSearchParameters(count, query);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(ct) ?? 0);
        }

        var messages = new List<MailMessageSummary>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT s.unique_id,s.account_id,s.folder_name,s.internet_message_id,s.from_disp,s.to_addr,
                   s.subject,s.date_ticks,s.is_read,s.preview_text,s.is_replied,s.is_forwarded,
                   s.has_attachments,s.is_mailing_list,s.flag_id
            FROM LocalMessageFts f
            JOIN MessageSummary s ON s.account_id=f.account_id AND s.unique_id=f.unique_id AND s.folder_name=f.folder_name
            WHERE LocalMessageFts MATCH $q{accountFilter}{folderFilter}
            ORDER BY {order} LIMIT $limit OFFSET $offset;
            """;
        AddSearchParameters(cmd, query);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) messages.Add(ReadLocalSummary(reader));
        return new LocalSearchResult(messages, total);
    }

    public async Task<LocalSearchResult> SearchLocalMessagesAdvancedAsync(AdvancedSearchQuery query, CancellationToken ct = default)
    {
        if (query.Criteria.Count == 0) return new LocalSearchResult([], 0);
        var predicates = new List<string>();
        await using var conn = await OpenAsync();
        await using var count = conn.CreateCommand();
        for (var i = 0; i < query.Criteria.Count; i++)
        {
            var criterion = query.Criteria[i];
            var join = i == 0 ? string.Empty : criterion.Join.Equals("OR", StringComparison.OrdinalIgnoreCase) ? " OR " : " AND ";
            var parameter = "$v" + i;
            string predicate;
            if (criterion.Field.Equals("Date", StringComparison.OrdinalIgnoreCase))
            {
                if (!DateTimeOffset.TryParse(criterion.Value, System.Globalization.CultureInfo.CurrentCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out var date))
                    throw new FormatException($"Invalid date: {criterion.Value}");
                var start = new DateTimeOffset(date.Date, date.Offset).ToUniversalTime();
                var end = start.AddDays(1);
                predicate = criterion.Operator switch
                {
                    "<" => $"s.date_ticks < {parameter}",
                    "<=" => $"s.date_ticks < {parameter}",
                    ">" => $"s.date_ticks >= {parameter}",
                    ">=" => $"s.date_ticks >= {parameter}",
                    _ => $"s.date_ticks >= {parameter} AND s.date_ticks < {parameter}_end",
                };
                count.Parameters.AddWithValue(parameter,
                    criterion.Operator is "<=" or ">" ? end.UtcTicks : start.UtcTicks);
                if (criterion.Operator is not ("<" or "<=" or ">" or ">="))
                    count.Parameters.AddWithValue(parameter + "_end", end.UtcTicks);
            }
            else
            {
                var column = criterion.Field.ToLowerInvariant() switch
                {
                    "from" => "from_addr", "to" => "to_addr", "cc" => "cc_addr",
                    "subject" => "subject", _ => "body_text",
                };
                predicate = $"LocalMessageFts.{column} MATCH {parameter}";
                count.Parameters.AddWithValue(parameter, "\"" + criterion.Value.Trim().Replace("\"", "\"\"") + "\"");
            }
            predicates.Add(join + predicate);
        }
        var scope = string.Empty;
        if (query.AccountId.HasValue) { scope += " AND s.account_id=$aid"; count.Parameters.AddWithValue("$aid", query.AccountId.Value.ToString()); }
        if (!string.IsNullOrWhiteSpace(query.FolderName))
        {
            scope += query.IncludeDescendants
                ? " AND (s.folder_name=$fn OR (s.folder_name >= $ds AND s.folder_name < $de))"
                : " AND s.folder_name=$fn";
            count.Parameters.AddWithValue("$fn", query.FolderName);
            if (query.IncludeDescendants) AddDescendantRange(count, query.FolderName);
        }
        var where = string.Concat(predicates);
        count.CommandText = $"SELECT count(*) FROM MessageSummary s JOIN LocalMessageFts ON LocalMessageFts.account_id=s.account_id AND LocalMessageFts.unique_id=s.unique_id AND LocalMessageFts.folder_name=s.folder_name WHERE ({where}){scope};";
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct) ?? 0);

        await using var cmd = conn.CreateCommand();
        foreach (SqliteParameter p in count.Parameters) cmd.Parameters.AddWithValue(p.ParameterName, p.Value);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, LocalMailConstants.MaxRenderedMessages));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        var order = query.Sort switch
        {
            LocalSearchSort.OldestFirst => "s.date_ticks ASC",
            LocalSearchSort.FromAscending => "s.from_disp COLLATE NOCASE ASC",
            LocalSearchSort.FromDescending => "s.from_disp COLLATE NOCASE DESC",
            LocalSearchSort.ToAscending => "s.to_addr COLLATE NOCASE ASC",
            LocalSearchSort.ToDescending => "s.to_addr COLLATE NOCASE DESC",
            LocalSearchSort.SubjectAscending => "s.subject COLLATE NOCASE ASC",
            LocalSearchSort.SubjectDescending => "s.subject COLLATE NOCASE DESC",
            LocalSearchSort.ReadAscending => "s.is_read ASC, s.date_ticks DESC",
            LocalSearchSort.ReadDescending => "s.is_read DESC, s.date_ticks DESC",
            LocalSearchSort.AttachmentsFirst => "s.has_attachments DESC, s.date_ticks DESC",
            LocalSearchSort.AttachmentsLast => "s.has_attachments ASC, s.date_ticks DESC",
            _ => "s.date_ticks DESC",
        };
        cmd.CommandText = $"""
            SELECT s.unique_id,s.account_id,s.folder_name,s.internet_message_id,s.from_disp,s.to_addr,
                   s.subject,s.date_ticks,s.is_read,s.preview_text,s.is_replied,s.is_forwarded,
                   s.has_attachments,s.is_mailing_list,s.flag_id
            FROM MessageSummary s JOIN LocalMessageFts ON LocalMessageFts.account_id=s.account_id AND LocalMessageFts.unique_id=s.unique_id AND LocalMessageFts.folder_name=s.folder_name
            WHERE ({where}){scope} ORDER BY {order} LIMIT $limit OFFSET $offset;
            """;
        var messages = new List<MailMessageSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) messages.Add(ReadLocalSummary(reader));
        return new LocalSearchResult(messages, total);
    }

    public async Task<LocalSearchResult> LoadLocalPageAsync(Guid? accountId, string? folderName, int limit, int offset,
        LocalSearchSort sort = LocalSearchSort.NewestFirst, bool includeDescendants = false,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, LocalMailConstants.MaxRenderedMessages);
        offset = Math.Max(0, offset);
        var accountFilter = accountId.HasValue ? " AND account_id=$aid" : string.Empty;
        var folderFilter = !string.IsNullOrWhiteSpace(folderName)
            ? includeDescendants ? " AND (folder_name=$fn OR (folder_name >= $ds AND folder_name < $de))" : " AND folder_name=$fn"
            : string.Empty;
        var order = sort switch
        {
            LocalSearchSort.OldestFirst => "date_ticks ASC",
            LocalSearchSort.FromAscending => "from_disp COLLATE NOCASE ASC",
            LocalSearchSort.FromDescending => "from_disp COLLATE NOCASE DESC",
            LocalSearchSort.ToAscending => "to_addr COLLATE NOCASE ASC",
            LocalSearchSort.ToDescending => "to_addr COLLATE NOCASE DESC",
            LocalSearchSort.SubjectAscending => "subject COLLATE NOCASE ASC",
            LocalSearchSort.SubjectDescending => "subject COLLATE NOCASE DESC",
            LocalSearchSort.ReadAscending => "is_read ASC, date_ticks DESC",
            LocalSearchSort.ReadDescending => "is_read DESC, date_ticks DESC",
            LocalSearchSort.AttachmentsFirst => "has_attachments DESC, date_ticks DESC",
            LocalSearchSort.AttachmentsLast => "has_attachments ASC, date_ticks DESC",
            _ => "date_ticks DESC",
        };
        await using var conn = await OpenAsync();
        await using var count = conn.CreateCommand();
        count.CommandText = $"SELECT count(*) FROM MessageSummary WHERE 1=1{accountFilter}{folderFilter};";
        if (accountId.HasValue) count.Parameters.AddWithValue("$aid", accountId.Value.ToString());
        if (!string.IsNullOrWhiteSpace(folderName)) count.Parameters.AddWithValue("$fn", folderName);
        if (!string.IsNullOrWhiteSpace(folderName) && includeDescendants) AddDescendantRange(count, folderName);
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct) ?? 0);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT unique_id,account_id,folder_name,internet_message_id,from_disp,to_addr,
                   subject,date_ticks,is_read,preview_text,is_replied,is_forwarded,
                   has_attachments,is_mailing_list,flag_id
            FROM MessageSummary WHERE 1=1{accountFilter}{folderFilter}
            ORDER BY {order} LIMIT $limit OFFSET $offset;
            """;
        if (accountId.HasValue) cmd.Parameters.AddWithValue("$aid", accountId.Value.ToString());
        if (!string.IsNullOrWhiteSpace(folderName)) cmd.Parameters.AddWithValue("$fn", folderName);
        if (!string.IsNullOrWhiteSpace(folderName) && includeDescendants) AddDescendantRange(cmd, folderName);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        var messages = new List<MailMessageSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) messages.Add(ReadLocalSummary(reader));
        return new LocalSearchResult(messages, total);
    }

    private static void AddDescendantRange(SqliteCommand command, string folderName)
    {
        command.Parameters.AddWithValue("$ds", folderName + "/");
        command.Parameters.AddWithValue("$de", folderName + "0");
    }

    private static void AddSearchParameters(SqliteCommand command, LocalSearchQuery query)
    {
        // Treat the search box as text, not as an FTS5 query-language console. Quoting also keeps
        // punctuation in addresses/subjects from producing syntax errors.
        command.Parameters.AddWithValue("$q", "\"" + query.Text.Trim().Replace("\"", "\"\"") + "\"");
        if (query.AccountId.HasValue) command.Parameters.AddWithValue("$aid", query.AccountId.Value.ToString());
        if (!string.IsNullOrWhiteSpace(query.FolderName)) command.Parameters.AddWithValue("$fn", query.FolderName);
        if (!string.IsNullOrWhiteSpace(query.FolderName) && query.IncludeDescendants)
            AddDescendantRange(command, query.FolderName);
    }

    private static MailMessageSummary ReadLocalSummary(SqliteDataReader reader) => new()
    {
        MessageId = reader.GetString(0), AccountId = Guid.Parse(reader.GetString(1)), FolderName = reader.GetString(2),
        InternetMessageId = reader.GetString(3), From = reader.GetString(4), To = reader.GetString(5),
        Subject = reader.GetString(6), Date = new DateTimeOffset(reader.GetInt64(7), TimeSpan.Zero),
        IsRead = reader.GetInt64(8) != 0, Preview = reader.GetString(9), IsReplied = reader.GetInt64(10) != 0,
        IsForwarded = reader.GetInt64(11) != 0, HasAttachments = reader.GetInt64(12) != 0,
        IsMailingList = reader.GetInt64(13) != 0, FlagId = reader.IsDBNull(14) ? null : reader.GetString(14),
    };

    public async Task MoveLocalMessagesAsync(Guid accountId, string sourceFolder, string destinationFolder,
        IReadOnlyCollection<string> messageIds, CancellationToken ct = default)
    {
        if (messageIds.Count == 0 || sourceFolder.Equals(destinationFolder, StringComparison.OrdinalIgnoreCase)) return;
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await EnsureNotContainerAsync(conn, tx, accountId, destinationFolder, allowMissing: false, ct);
        foreach (var id in messageIds)
        {
            foreach (var table in new[] { "MessageSummary", "MessageDetail" })
                await ExecuteKeyMutationAsync(conn, tx,
                    $"UPDATE {table} SET folder_name=$dest WHERE account_id=$aid AND folder_name=$source AND unique_id=$uid;",
                    accountId, sourceFolder, id, destinationFolder, ct);
            await ExecuteKeyMutationAsync(conn, tx,
                "UPDATE LocalMessageFts SET folder_name=$dest WHERE rowid IN " +
                "(SELECT fts_rowid FROM LocalMessageFtsKey WHERE account_id=$aid AND folder_name=$source AND unique_id=$uid);",
                accountId, sourceFolder, id, destinationFolder, ct);
            await ExecuteKeyMutationAsync(conn, tx,
                "UPDATE LocalMessageFtsKey SET folder_name=$dest WHERE account_id=$aid AND folder_name=$source AND unique_id=$uid;",
                accountId, sourceFolder, id, destinationFolder, ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task DeleteLocalMessagesAsync(Guid accountId, string folderName,
        IReadOnlyCollection<string> messageIds, CancellationToken ct = default)
    {
        if (messageIds.Count == 0) return;
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        // FTS5 cannot index its UNINDEXED identity columns. Deleting each id separately therefore
        // scanned the complete FTS table once per selected message (19 messages = 19 scans of
        // ~430k rows). One IN statement performs a single scan per bounded chunk.
        const int chunkSize = 400;
        var ids = messageIds.ToList();
        for (var offset = 0; offset < ids.Count; offset += chunkSize)
        {
            var chunk = ids.Skip(offset).Take(chunkSize).ToList();
            var placeholders = string.Join(',', Enumerable.Range(0, chunk.Count).Select(i => $"$u{i}"));
            await using var command = conn.CreateCommand();
            command.Transaction = tx;
            command.CommandText =
                $"DELETE FROM LocalMessageFts WHERE rowid IN (SELECT fts_rowid FROM LocalMessageFtsKey WHERE account_id=$aid AND folder_name=$source AND unique_id IN ({placeholders}));" +
                $"DELETE FROM LocalMessageFtsKey WHERE account_id=$aid AND folder_name=$source AND unique_id IN ({placeholders});" +
                $"DELETE FROM MessageDetail WHERE account_id=$aid AND folder_name=$source AND unique_id IN ({placeholders});" +
                $"DELETE FROM MessageSummary WHERE account_id=$aid AND folder_name=$source AND unique_id IN ({placeholders});";
            command.Parameters.AddWithValue("$aid", accountId.ToString());
            command.Parameters.AddWithValue("$source", folderName);
            for (var i = 0; i < chunk.Count; i++) command.Parameters.AddWithValue($"$u{i}", chunk[i]);
            await command.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static async Task ExecuteKeyMutationAsync(SqliteConnection conn, SqliteTransaction tx, string sql,
        Guid accountId, string source, string id, string? destination, CancellationToken ct)
    {
        await using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$aid", accountId.ToString());
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$uid", id);
        if (destination is not null) command.Parameters.AddWithValue("$dest", destination);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureNotContainerAsync(SqliteConnection conn, SqliteTransaction tx,
        Guid accountId, string folderName, bool allowMissing, CancellationToken ct)
    {
        await using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT is_container FROM Folder WHERE account_id=$aid AND full_name=$fn;";
        command.Parameters.AddWithValue("$aid", accountId.ToString());
        command.Parameters.AddWithValue("$fn", folderName);
        var result = await command.ExecuteScalarAsync(ct);
        if (result is null && !allowMissing) throw new InvalidOperationException("Destination folder does not exist.");
        if (result is not null && Convert.ToInt64(result) != 0)
            throw new InvalidOperationException("A container folder cannot contain messages.");
    }
}
