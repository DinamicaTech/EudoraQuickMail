using Microsoft.Data.Sqlite;
using QuickMail.Models;

namespace QuickMail.Services;

public partial class LocalStoreService
{
    public async Task RefreshLocalFolderCountsAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync();
        await using var command = conn.CreateCommand();
        command.CommandText = """
            UPDATE Folder SET
                message_count=(SELECT COUNT(*) FROM MessageSummary s
                               WHERE s.account_id=Folder.account_id AND s.folder_name=Folder.full_name),
                unread_count=(SELECT COUNT(*) FROM MessageSummary s
                              WHERE s.account_id=Folder.account_id AND s.folder_name=Folder.full_name AND s.is_read=0)
            WHERE account_id=$aid;
            """;
        command.Parameters.AddWithValue("$aid", accountId.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

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
        // A locally-authored message is authoritative. Ensure its folder is catalogued in the same
        // transaction so a scheduled message or draft can never exist in SQLite while remaining
        // invisible from the folder tree.
        await using (var folder = conn.CreateCommand())
        {
            folder.Transaction = tx;
            folder.CommandText = """
                INSERT OR IGNORE INTO Folder
                    (account_id,full_name,display_name,parent_id,kind,exclude_from_all_mail,
                     unread_count,message_count,sort_order,is_container)
                VALUES ($aid,$fn,$display,NULL,$kind,$exclude,0,0,0,0);
                """;
            folder.Parameters.AddWithValue("$aid", message.AccountId.ToString());
            folder.Parameters.AddWithValue("$fn", message.FolderName);
            folder.Parameters.AddWithValue("$display", message.FolderName.Split('/', '.')[^1]);
            var kind = message.FolderName.Equals("Draft", StringComparison.OrdinalIgnoreCase)
                || message.FolderName.Equals("Drafts", StringComparison.OrdinalIgnoreCase)
                    ? SpecialFolderKind.Drafts
                : message.FolderName.Equals("Scheduled", StringComparison.OrdinalIgnoreCase)
                    ? SpecialFolderKind.Scheduled
                : message.FolderName.Equals("Sent", StringComparison.OrdinalIgnoreCase)
                    ? SpecialFolderKind.Sent
                : message.FolderName.Equals("Trash", StringComparison.OrdinalIgnoreCase)
                    ? SpecialFolderKind.Trash
                : message.FolderName.Equals("Inbox", StringComparison.OrdinalIgnoreCase)
                    ? SpecialFolderKind.Inbox
                : SpecialFolderKind.None;
            folder.Parameters.AddWithValue("$kind", (int)kind);
            folder.Parameters.AddWithValue("$exclude", kind is SpecialFolderKind.Drafts
                or SpecialFolderKind.Scheduled or SpecialFolderKind.Sent or SpecialFolderKind.Trash ? 1 : 0);
            await folder.ExecuteNonQueryAsync(ct);
        }
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
                    (unique_id,account_id,folder_name,to_addr,cc,reply_to,plain_body,html_body,attachments_json,calendar_ics,raw_headers,draft_compose_mode,draft_spell_language)
                VALUES ($uid,$aid,$fn,$to,$cc,$reply,$plain,$html,NULL,NULL,$headers,$mode,$language)
                ON CONFLICT(unique_id,account_id,folder_name) DO UPDATE SET
                    to_addr=excluded.to_addr,cc=excluded.cc,reply_to=excluded.reply_to,
                    plain_body=excluded.plain_body,html_body=excluded.html_body,
                    attachments_json=NULL,calendar_ics=NULL,raw_headers=excluded.raw_headers,
                    draft_compose_mode=excluded.draft_compose_mode,
                    draft_spell_language=excluded.draft_spell_language;
                """;
            AddMessageKey(detail, message);
            detail.Parameters.AddWithValue("$to", message.To ?? string.Empty);
            detail.Parameters.AddWithValue("$cc", message.Cc ?? string.Empty);
            detail.Parameters.AddWithValue("$reply", message.ReplyTo ?? string.Empty);
            detail.Parameters.AddWithValue("$plain", message.PlainTextBody ?? string.Empty);
            detail.Parameters.AddWithValue("$html", message.HtmlBody ?? string.Empty);
            detail.Parameters.AddWithValue("$headers", message.RawHeaders ?? string.Empty);
            detail.Parameters.AddWithValue("$mode", (int)message.DraftComposeMode);
            detail.Parameters.AddWithValue("$language", message.DraftSpellLanguage ?? string.Empty);
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
        await UpdateFolderCountsAsync(conn, tx, message.AccountId, message.FolderName, ct);
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
        var parsed = QuickSearchParser.Parse(query.Text);
        var terms = parsed.Groups.SelectMany(g => g.Alternatives).ToList();
        var requiresFts = terms.Any(t => t.Field is QuickSearchField.Any or QuickSearchField.To
            or QuickSearchField.From or QuickSearchField.Cc or QuickSearchField.Subject
            or QuickSearchField.Body);
        var requiresDetail = terms.Any(t => t.Field is QuickSearchField.AttachmentCount
            or QuickSearchField.AttachmentName);
        var ftsJoin = requiresFts
            ? "JOIN LocalMessageFts f ON f.account_id=s.account_id AND f.unique_id=s.unique_id AND f.folder_name=s.folder_name"
            : string.Empty;
        var detailJoin = requiresDetail
            ? "LEFT JOIN MessageDetail d ON d.account_id=s.account_id AND d.unique_id=s.unique_id AND d.folder_name=s.folder_name"
            : string.Empty;
        var limit = Math.Clamp(query.Limit, 1, LocalMailConstants.MaxRenderedMessages);
        var accountFilter = BuildAccountFilter("s.", query.AccountId, query.AccountIds);
        var folderFilter = !string.IsNullOrWhiteSpace(query.FolderName)
            ? query.IncludeDescendants ? " AND (s.folder_name=$fn OR (s.folder_name >= $ds AND s.folder_name < $de))" : " AND s.folder_name=$fn"
            : string.Empty;
        var order = query.Sort switch
        {
            LocalSearchSort.OldestFirst => "s.date_ticks ASC",
            LocalSearchSort.Relevance when requiresFts => "bm25(LocalMessageFts)",
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
        await using var predicateCommand = conn.CreateCommand();
        var predicate = BuildQuickSearchPredicate(parsed, predicateCommand);
        long total;
        await using (var count = conn.CreateCommand())
        {
            CopyParameters(predicateCommand, count);
            count.CommandText = $"""
                SELECT count(*) FROM MessageSummary s
                {ftsJoin}
                {detailJoin}
                WHERE ({predicate}){accountFilter}{folderFilter};
                """;
            AddScopeParameters(count, query);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(ct) ?? 0);
        }

        var messages = new List<MailMessageSummary>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT s.unique_id,s.account_id,s.folder_name,s.internet_message_id,s.from_disp,s.to_addr,
                   s.subject,s.date_ticks,s.is_read,s.preview_text,s.is_replied,s.is_forwarded,
                   s.has_attachments,s.is_mailing_list,s.flag_id
            FROM MessageSummary s
            {ftsJoin}
            {detailJoin}
            WHERE ({predicate}){accountFilter}{folderFilter}
            ORDER BY {order} LIMIT $limit OFFSET $offset;
            """;
        CopyParameters(predicateCommand, cmd);
        AddScopeParameters(cmd, query);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) messages.Add(ReadLocalSummary(reader));
        return new LocalSearchResult(messages, total);
    }

    private static string BuildQuickSearchPredicate(ParsedQuickSearch query, SqliteCommand command)
    {
        var groups = new List<string>(); var parameterIndex = 0;
        foreach (var group in query.Groups)
        {
            var alternatives = new List<string>();
            foreach (var term in group.Alternatives)
            {
                if (term.Field == QuickSearchField.Unread)
                {
                    alternatives.Add("s.is_read=0");
                    continue;
                }
                if (term.Field == QuickSearchField.Flagged)
                {
                    alternatives.Add("s.flag_id IS NOT NULL");
                    continue;
                }
                var p = "$q" + parameterIndex++;
                if (term.Field == QuickSearchField.AttachmentCount)
                {
                    if (!int.TryParse(term.Value, out var count) || count < 0)
                        throw new FormatException($"Invalid attachment count '{term.Value}'.");
                    command.Parameters.AddWithValue(p, count);
                    alternatives.Add($"COALESCE(json_array_length(d.attachments_json),0) {term.Operator} {p}");
                    continue;
                }
                if (term.Field == QuickSearchField.Date)
                {
                    var (start, end) = QuickSearchParser.ParseDateRange(term.Value);
                    if (term.Operator == "=")
                    {
                        command.Parameters.AddWithValue(p, start.UtcTicks);
                        command.Parameters.AddWithValue(p + "e", end.UtcTicks);
                        alternatives.Add($"(s.date_ticks >= {p} AND s.date_ticks < {p}e)");
                    }
                    else
                    {
                        var boundary = term.Operator is ">" or "<=" ? end.UtcTicks : start.UtcTicks;
                        command.Parameters.AddWithValue(p, boundary);
                        var op = term.Operator switch { ">" => ">=", "<=" => "<", _ => term.Operator };
                        alternatives.Add($"s.date_ticks {op} {p}");
                    }
                    continue;
                }

                var wildcard = term.Value.Contains('?');
                var value = wildcard ? "%" + EscapeLike(term.Value).Replace("?", "_") + "%"
                    : "\"" + term.Value.Replace("\"", "\"\"") + "\"";
                command.Parameters.AddWithValue(p, value);
                if (term.Field is QuickSearchField.AttachmentName or QuickSearchField.AttachmentContent)
                    command.Parameters.AddWithValue(p + "l", "%" + EscapeLike(term.Value).Replace("?", "_") + "%");
                string FieldPredicate(string column, string ftsColumn) => wildcard
                    ? $"{column} LIKE {p} ESCAPE '\\' COLLATE NOCASE"
                    : $"f.rowid IN (SELECT rowid FROM LocalMessageFts WHERE LocalMessageFts MATCH '{ftsColumn}:' || {p})";
                alternatives.Add(term.Field switch
                {
                    QuickSearchField.To => FieldPredicate("f.to_addr", "to_addr"),
                    QuickSearchField.From => FieldPredicate("f.from_addr", "from_addr"),
                    QuickSearchField.Cc => FieldPredicate("f.cc_addr", "cc_addr"),
                    QuickSearchField.Subject => FieldPredicate("f.subject", "subject"),
                    QuickSearchField.Body => FieldPredicate("f.body_text", "body_text"),
                    QuickSearchField.AttachmentName =>
                        $"(EXISTS(SELECT 1 FROM json_each(d.attachments_json) j WHERE json_extract(j.value,'$.FileName') LIKE {p}l ESCAPE '\\' COLLATE NOCASE) OR EXISTS(SELECT 1 FROM AttachmentContent ac WHERE ac.account_id=s.account_id AND ac.unique_id=s.unique_id AND ac.folder_name=s.folder_name AND ac.entry_path LIKE {p}l ESCAPE '\\' COLLATE NOCASE))",
                    QuickSearchField.AttachmentContent =>
                        $"(s.has_attachments=1 AND (s.account_id,s.unique_id,s.folder_name) IN " +
                        $"(SELECT ac.account_id,ac.unique_id,ac.folder_name FROM AttachmentContent ac " +
                        $"WHERE ac.status='indexed' AND ac.content_text LIKE {p}l ESCAPE '\\' COLLATE NOCASE))",
                    _ => wildcard
                        ? $"(f.from_addr LIKE {p} ESCAPE '\\' COLLATE NOCASE OR f.to_addr LIKE {p} ESCAPE '\\' COLLATE NOCASE OR f.cc_addr LIKE {p} ESCAPE '\\' COLLATE NOCASE OR f.subject LIKE {p} ESCAPE '\\' COLLATE NOCASE OR f.body_text LIKE {p} ESCAPE '\\' COLLATE NOCASE)"
                        : $"f.rowid IN (SELECT rowid FROM LocalMessageFts WHERE LocalMessageFts MATCH {p})",
                });
            }
            groups.Add("(" + string.Join(" OR ", alternatives) + ")");
        }
        return groups.Count == 0 ? "1=1" : string.Join(" AND ", groups);
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    private static void CopyParameters(SqliteCommand source, SqliteCommand destination)
    { foreach (SqliteParameter p in source.Parameters) destination.Parameters.AddWithValue(p.ParameterName, p.Value); }
    private static void AddScopeParameters(SqliteCommand command, LocalSearchQuery query)
    {
        AddAccountParameters(command, query.AccountId, query.AccountIds);
        if (!string.IsNullOrWhiteSpace(query.FolderName)) command.Parameters.AddWithValue("$fn", query.FolderName);
        if (!string.IsNullOrWhiteSpace(query.FolderName) && query.IncludeDescendants) AddDescendantRange(command, query.FolderName);
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
        scope += BuildAccountFilter("s.", query.AccountId, query.AccountIds);
        AddAccountParameters(count, query.AccountId, query.AccountIds);
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
        CancellationToken ct = default, IReadOnlyCollection<Guid>? accountIds = null)
    {
        limit = Math.Clamp(limit, 1, LocalMailConstants.MaxRenderedMessages);
        offset = Math.Max(0, offset);
        var accountFilter = BuildAccountFilter(string.Empty, accountId, accountIds);
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
        AddAccountParameters(count, accountId, accountIds);
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
        AddAccountParameters(cmd, accountId, accountIds);
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

    private static string BuildAccountFilter(string prefix, Guid? accountId, IReadOnlyCollection<Guid>? accountIds)
    {
        if (accountId.HasValue) return $" AND {prefix}account_id=$aid";
        if (accountIds is not { Count: > 0 }) return string.Empty;
        return $" AND {prefix}account_id IN ({string.Join(',', accountIds.Select((_, i) => "$a" + i))})";
    }

    private static void AddAccountParameters(SqliteCommand command, Guid? accountId, IReadOnlyCollection<Guid>? accountIds)
    {
        if (accountId.HasValue)
        {
            command.Parameters.AddWithValue("$aid", accountId.Value.ToString());
            return;
        }
        if (accountIds is null) return;
        var i = 0;
        foreach (var id in accountIds)
            command.Parameters.AddWithValue("$a" + i++, id.ToString());
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
            // A previous move of the same local identity may already exist in Trash (for
            // example after an interrupted refresh). Make the move idempotent instead of letting
            // the destination primary key roll the transaction back and resurrect the source row.
            await ExecuteKeyMutationAsync(conn, tx,
                "DELETE FROM LocalMessageFts WHERE rowid IN " +
                "(SELECT fts_rowid FROM LocalMessageFtsKey WHERE account_id=$aid AND folder_name=$source AND unique_id=$uid);",
                accountId, destinationFolder, id, destinationFolder, ct);
            await ExecuteKeyMutationAsync(conn, tx,
                "DELETE FROM AttachmentContentFts WHERE rowid IN " +
                "(SELECT fts_rowid FROM AttachmentContent WHERE account_id=$aid AND folder_name=$source AND unique_id=$uid);",
                accountId, destinationFolder, id, destinationFolder, ct);
            foreach (var table in new[] { "LocalMessageFtsKey", "AttachmentContent", "MessageDetail", "MessageSummary" })
                await ExecuteKeyMutationAsync(conn, tx,
                    $"DELETE FROM {table} WHERE account_id=$aid AND folder_name=$source AND unique_id=$uid;",
                    accountId, destinationFolder, id, destinationFolder, ct);

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
            await ExecuteKeyMutationAsync(conn, tx,
                "UPDATE AttachmentContent SET folder_name=$dest WHERE account_id=$aid AND folder_name=$source AND unique_id=$uid;",
                accountId, sourceFolder, id, destinationFolder, ct);
        }
        await UpdateFolderCountsAsync(conn, tx, accountId, sourceFolder, ct);
        await UpdateFolderCountsAsync(conn, tx, accountId, destinationFolder, ct);
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
                $"DELETE FROM AttachmentContentFts WHERE rowid IN (SELECT fts_rowid FROM AttachmentContent WHERE account_id=$aid AND folder_name=$source AND unique_id IN ({placeholders}));" +
                $"DELETE FROM AttachmentContent WHERE account_id=$aid AND folder_name=$source AND unique_id IN ({placeholders});" +
                $"DELETE FROM MessageDetail WHERE account_id=$aid AND folder_name=$source AND unique_id IN ({placeholders});" +
                $"DELETE FROM MessageSummary WHERE account_id=$aid AND folder_name=$source AND unique_id IN ({placeholders});";
            command.Parameters.AddWithValue("$aid", accountId.ToString());
            command.Parameters.AddWithValue("$source", folderName);
            for (var i = 0; i < chunk.Count; i++) command.Parameters.AddWithValue($"$u{i}", chunk[i]);
            await command.ExecuteNonQueryAsync(ct);
        }
        await UpdateFolderCountsAsync(conn, tx, accountId, folderName, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task UpdateFolderCountsAsync(SqliteConnection conn, SqliteTransaction tx,
        Guid accountId, string folderName, CancellationToken ct)
    {
        await using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE Folder SET
                message_count=(SELECT COUNT(*) FROM MessageSummary
                               WHERE account_id=$aid AND folder_name=$fn),
                unread_count=(SELECT COUNT(*) FROM MessageSummary
                              WHERE account_id=$aid AND folder_name=$fn AND is_read=0)
            WHERE account_id=$aid AND full_name=$fn;
            """;
        command.Parameters.AddWithValue("$aid", accountId.ToString());
        command.Parameters.AddWithValue("$fn", folderName);
        await command.ExecuteNonQueryAsync(ct);
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
