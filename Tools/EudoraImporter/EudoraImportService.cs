using Microsoft.Data.Sqlite;
using MimeKit;
using System.Text.Json;
using System.Text;

namespace EudoraImporter;

internal sealed record ImportProgress(int MailboxesCompleted, int MailboxCount, string Mailbox, long MessagesImported);
internal sealed record ImportResult(long MessageCount, long ErrorCount);

internal static class EudoraImportService
{
    public static async Task<ImportResult> ImportAsync(string sourceDirectory, string outputFile,
        IReadOnlyList<EudoraMailbox> mailboxes, Action<ImportProgress>? report = null,
        CancellationToken cancellationToken = default)
    {
        await using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = outputFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // The finished temporary database is atomically renamed by the caller. On Windows,
            // a pooled connection keeps the file handle open after this method returns.
            Pooling = false,
        }.ToString());
        await database.OpenAsync(cancellationToken);
        await CreateSchemaAsync(database, sourceDirectory, cancellationToken);

        long imported = 0, errors = 0;
        for (var mailboxIndex = 0; mailboxIndex < mailboxes.Count; mailboxIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mailbox = mailboxes[mailboxIndex];
            var result = await ImportMailboxAsync(database, mailbox, sourceDirectory, cancellationToken);
            imported += result.Imported;
            errors += result.Errors;
            report?.Invoke(new ImportProgress(mailboxIndex + 1, mailboxes.Count, mailbox.DisplayName, result.Imported));
        }
        await ExecuteAsync(database, "INSERT INTO messages_fts(messages_fts) VALUES('optimize');", cancellationToken);
        return new ImportResult(imported, errors);
    }

    private static async Task<(long Imported, long Errors)> ImportMailboxAsync(SqliteConnection database,
        EudoraMailbox mailbox, string sourceDirectory, CancellationToken cancellationToken)
    {
        await using var transaction = await database.BeginTransactionAsync(cancellationToken);
        await using var insert = database.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO messages
                (source_mailbox, source_ordinal, mailbox, message_id, date_utc, from_addr,
                 to_addr, cc_addr, bcc_addr, subject, raw_headers, body_text, body_html, attachments_json, is_read)
            VALUES ($source, $ordinal, $mailbox, $message_id, $date, $from,
                    $to, $cc, $bcc, $subject, $headers, $body, $html, $attachments, $read);
            """;
        var source = insert.Parameters.Add("$source", SqliteType.Text);
        var ordinal = insert.Parameters.Add("$ordinal", SqliteType.Integer);
        var display = insert.Parameters.Add("$mailbox", SqliteType.Text);
        var messageId = insert.Parameters.Add("$message_id", SqliteType.Text);
        var date = insert.Parameters.Add("$date", SqliteType.Text);
        var from = insert.Parameters.Add("$from", SqliteType.Text);
        var to = insert.Parameters.Add("$to", SqliteType.Text);
        var cc = insert.Parameters.Add("$cc", SqliteType.Text);
        var bcc = insert.Parameters.Add("$bcc", SqliteType.Text);
        var subject = insert.Parameters.Add("$subject", SqliteType.Text);
        var headers = insert.Parameters.Add("$headers", SqliteType.Text);
        var body = insert.Parameters.Add("$body", SqliteType.Text);
        var html = insert.Parameters.Add("$html", SqliteType.Text);
        var attachments = insert.Parameters.Add("$attachments", SqliteType.Text);
        var read = insert.Parameters.Add("$read", SqliteType.Integer);
        source.Value = mailbox.RelativePath;
        display.Value = mailbox.DisplayName;

        long imported = 0, errors = 0;
        var options = ParserOptions.Default.Clone();
        // Eudora writes Content-Length on newer mailbox entries. Honouring it is essential:
        // treating every line-start "From " as the next mbox separator parsed the headers but
        // produced empty bodies for post-2022 messages.
        options.RespectContentLength = true;
        await using var stream = new FileStream(mailbox.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using var rawStream = new FileStream(mailbox.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.RandomAccess | FileOptions.Asynchronous);
        var parser = new MimeParser(options, stream, MimeFormat.Mbox, persistent: false);
        FileStream? recoveryStream = null;
        while (!parser.IsEndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentOrdinal = imported + errors + 1;
            var positionBefore = parser.Position;
            try
            {
                var message = await parser.ParseMessageAsync(cancellationToken);
                if (parser.Position <= positionBefore)
                    throw new InvalidDataException(
                        $"El analizador no avanzó desde la posición {positionBefore}; se abandona el resto del buzón para evitar un bucle.");
                ordinal.Value = currentOrdinal;
                messageId.Value = message.MessageId ?? string.Empty;
                var messageDate = FindMessageDate(message, parser.MboxMarker);
                date.Value = messageDate == DateTimeOffset.MinValue ? DBNull.Value : messageDate.UtcDateTime.ToString("O");
                subject.Value = message.Subject ?? string.Empty;
                var extracted = MessageTextExtractor.ExtractAll(message, sourceDirectory, mailbox.RelativePath);
                var rawMessage = await ReadRawMessageAsync(rawStream, positionBefore, parser.Position, cancellationToken);
                // Eudora sometimes stores an HTML document in its private <x-html> wrapper while
                // leaving a multipart Content-Type whose declared boundary never occurs. MimeKit
                // quite correctly finds no MIME body parts in that damaged message. Recover the
                // wrapped document itself rather than exposing the inner MIME headers and HTML
                // source as plain text in QuickMail.
                var wrappedHtml = MessageTextExtractor.ExtractEudoraWrappedHtml(rawMessage);
                if (wrappedHtml is not null && string.IsNullOrWhiteSpace(extracted.Html))
                    extracted = wrappedHtml;
                // MimeKit correctly rejects malformed RFC addresses and stops parsing headers at
                // the first blank line. Real Eudora mailboxes nevertheless contain useful values
                // such as "Softaculous <admin@>" and messages whose From/To block follows an
                // accidental blank line. Preserve those literal headers when the strict parse has
                // no value; an importer must not silently discard source data merely because it is
                // unsuitable for sending a new message.
                from.Value = ParsedOrRawAddress(message.From.ToString(), rawMessage,
                    "From", "X-Envelope-From", "Return-Path");
                to.Value = ParsedOrRawAddress(message.To.ToString(), rawMessage,
                    "To", "Delivered-To", "X-Rcpt-To", "X-MDRcpt-To", "X-MDaemon-Deliver-To", "Apparently-To");
                cc.Value = ParsedOrRawAddress(message.Cc.ToString(), rawMessage, "Cc");
                bcc.Value = ParsedOrRawAddress(message.Bcc.ToString(), rawMessage, "Bcc");
                headers.Value = ExtractRawHeaders(rawMessage);
                if (string.IsNullOrWhiteSpace(extracted.PlainText) && string.IsNullOrWhiteSpace(extracted.Html))
                {
                    var rawBody = ExtractRawBody(rawMessage);
                    extracted = rawBody.TrimStart().StartsWith('<')
                        ? new MessageTextExtractor.ExtractedMessage(
                            System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(rawBody, "<[^>]+>", " ")),
                            rawBody, [])
                        : new MessageTextExtractor.ExtractedMessage(rawBody, string.Empty, []);
                }
                var rawAttachments = MessageTextExtractor.ExtractAttachmentPaths(rawMessage, sourceDirectory,
                    mailbox.RelativePath);
                var attachmentPaths = extracted.AttachmentPaths.Concat(rawAttachments)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                body.Value = extracted.PlainText;
                html.Value = extracted.Html;
                attachments.Value = attachmentPaths.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(
                    attachmentPaths.Select(path => new
                    {
                        FileName = Path.GetFileName(path),
                        ContentType = ContentTypeFor(path),
                        FileSize = File.Exists(path) ? new FileInfo(path).Length : 0L,
                        PartSpecifier = path,
                    }));
                read.Value = IsRead(message) ? 1 : 0;
                await insert.ExecuteNonQueryAsync(cancellationToken);
                imported++;
            }
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                errors++;
                await RecordErrorAsync(database, (SqliteTransaction)transaction, mailbox.RelativePath,
                    currentOrdinal, ex.Message, cancellationToken);
                // A damaged message must not discard every newer message in the mailbox. Locate
                // the next line-start mbox separator using the independent random-access stream,
                // then recreate MimeKit's parser so none of its buffered state is reused.
                var next = await FindNextMboxMarkerAsync(rawStream, positionBefore + 1, cancellationToken);
                if (next < 0) break;
                // Never reuse the stream MimeKit failed on: its read-ahead buffer can make the
                // replacement parser see headers but empty bodies. A fresh file handle starts at
                // the exact recovered separator and owns independent buffering.
                if (recoveryStream is not null) await recoveryStream.DisposeAsync();
                recoveryStream = new FileStream(mailbox.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
                recoveryStream.Position = next;
                parser = new MimeParser(options, recoveryStream, MimeFormat.Mbox, persistent: false);
            }
        }
        if (recoveryStream is not null) await recoveryStream.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return (imported, errors);
    }

    private static async Task<long> FindNextMboxMarkerAsync(FileStream stream, long start, CancellationToken ct)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = new byte[bufferSize + 6];
        stream.Position = Math.Max(0, start);
        var carry = 0;
        long bufferStart = stream.Position;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(carry, bufferSize), ct);
            if (read == 0) return -1;
            var length = carry + read;
            for (var i = 0; i + 5 < length; i++)
                if ((i == 0 ? bufferStart == 0 : buffer[i - 1] == (byte)'\n')
                    && buffer[i] == (byte)'F' && buffer[i + 1] == (byte)'r'
                    && buffer[i + 2] == (byte)'o' && buffer[i + 3] == (byte)'m'
                    && buffer[i + 4] == (byte)' ')
                    return bufferStart + i;
            carry = Math.Min(6, length);
            Buffer.BlockCopy(buffer, length - carry, buffer, 0, carry);
            bufferStart += length - carry;
        }
    }

    private static async Task<string> ReadRawMessageAsync(FileStream stream, long start,
        long end, CancellationToken ct)
    {
        var length = end - start;
        if (length <= 0 || length > int.MaxValue) return string.Empty;
        var buffer = new byte[(int)length];
        stream.Position = start;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (count == 0) break;
            read += count;
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252).GetString(buffer, 0, read);
    }

    private static string ExtractRawBody(string raw)
    {
        var markerEnd = raw.IndexOf('\n');
        if (markerEnd < 0) return string.Empty;
        var headerEnd = raw.IndexOf("\r\n\r\n", markerEnd, StringComparison.Ordinal);
        var separatorLength = 4;
        if (headerEnd < 0)
        {
            headerEnd = raw.IndexOf("\n\n", markerEnd, StringComparison.Ordinal);
            separatorLength = 2;
        }
        return headerEnd < 0 ? string.Empty : raw[(headerEnd + separatorLength)..].Trim();
    }

    private static string ExtractRawHeaders(string raw)
    {
        var markerEnd = raw.IndexOf('\n');
        var start = raw.StartsWith("From ", StringComparison.Ordinal) && markerEnd >= 0 ? markerEnd + 1 : 0;
        var separator = raw.IndexOf("\r\n\r\n", start, StringComparison.Ordinal);
        if (separator < 0)
        {
            separator = raw.IndexOf("\n\n", start, StringComparison.Ordinal);
        }
        return separator < 0 ? string.Empty : raw.Substring(start, separator - start).TrimEnd('\r', '\n');
    }

    private static string ParsedOrRawAddress(string parsed, string raw, params string[] headerNames)
    {
        if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
        foreach (var name in headerNames)
        {
            var match = System.Text.RegularExpressions.Regex.Match(raw,
                $@"(?im)^{System.Text.RegularExpressions.Regex.Escape(name)}[ \t]*:[ \t]*(?<value>[^\r\n]*(?:\r?\n[ \t]+[^\r\n]*)*)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
            if (!match.Success) continue;
            var value = System.Text.RegularExpressions.Regex.Replace(match.Groups["value"].Value,
                @"\r?\n[ \t]+", " ").Trim();
            if (value.Length > 0) return value;
        }
        return string.Empty;
    }

    private static DateTimeOffset FindMessageDate(MimeMessage message, string? mboxMarker)
    {
        if (message.Date != DateTimeOffset.MinValue) return message.Date;
        foreach (var name in new[] { "Date", "X-Received-Date", "X-Sent-Date", "X-Date" })
            if (DateTimeOffset.TryParse(message.Headers[name], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var parsed)) return parsed;
        foreach (var received in message.Headers.Where(h => h.Id == HeaderId.Received).Reverse())
        {
            var separator = received.Value.LastIndexOf(';');
            if (separator >= 0 && DateTimeOffset.TryParse(received.Value[(separator + 1)..].Trim(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var parsed)) return parsed;
        }
        // Eudora's Out mailbox commonly omits the RFC Date header. Its mbox separator is then
        // authoritative: "From ???@??? Mon Dec 30 11:27:59 2013".
        var marker = mboxMarker ?? string.Empty;
        var markerDate = System.Text.RegularExpressions.Regex.Match(marker,
            @"\b(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)\s+[A-Z][a-z]{2}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}\s+\d{4}\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant).Value;
        if (DateTime.TryParseExact(markerDate, new[] { "ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var local))
            return new DateTimeOffset(local);
        // Eudora-generated IDs normally embed the local send timestamp, e.g.
        // <7.1.0.9.2.20131230112721.1ca17160@host>. This covers damaged/missing mbox markers.
        var idDate = System.Text.RegularExpressions.Regex.Match(message.MessageId ?? string.Empty,
            @"(?<!\d)(?<stamp>(?:19|20)\d{12})(?!\d)").Groups["stamp"].Value;
        if (DateTime.TryParseExact(idDate, "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out local))
            return new DateTimeOffset(local);
        return DateTimeOffset.MinValue;
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".doc" => "application/msword", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".txt" => "text/plain",
        ".zip" => "application/zip", _ => "application/octet-stream",
    };

    private static bool IsRead(MimeMessage message)
    {
        var status = message.Headers[HeaderId.Status] ?? message.Headers["X-Status"] ?? string.Empty;
        return status.Contains('R', StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RecordErrorAsync(SqliteConnection database, SqliteTransaction transaction,
        string mailbox, long ordinal, string error, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO import_errors(source_mailbox, source_ordinal, error) VALUES($m, $o, $e);";
        command.Parameters.AddWithValue("$m", mailbox);
        command.Parameters.AddWithValue("$o", ordinal);
        command.Parameters.AddWithValue("$e", error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CreateSchemaAsync(SqliteConnection database, string source, CancellationToken ct)
    {
        await ExecuteAsync(database, """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE messages (
                id INTEGER PRIMARY KEY, source_mailbox TEXT NOT NULL, source_ordinal INTEGER NOT NULL,
                mailbox TEXT NOT NULL, message_id TEXT NOT NULL DEFAULT '', date_utc TEXT,
                from_addr TEXT NOT NULL DEFAULT '', to_addr TEXT NOT NULL DEFAULT '',
                cc_addr TEXT NOT NULL DEFAULT '', bcc_addr TEXT NOT NULL DEFAULT '', subject TEXT NOT NULL DEFAULT '', raw_headers TEXT NOT NULL DEFAULT '',
                body_text TEXT NOT NULL DEFAULT '', body_html TEXT NOT NULL DEFAULT '',
                attachments_json TEXT, is_read INTEGER NOT NULL DEFAULT 0,
                UNIQUE(source_mailbox, source_ordinal)
            );
            CREATE INDEX messages_date ON messages(date_utc DESC);
            CREATE INDEX messages_mailbox ON messages(mailbox);
            CREATE VIRTUAL TABLE messages_fts USING fts5(
                subject, from_addr, to_addr, cc_addr, body_text,
                content='messages', content_rowid='id', tokenize='unicode61 remove_diacritics 2'
            );
            CREATE TRIGGER messages_ai AFTER INSERT ON messages BEGIN
                INSERT INTO messages_fts(rowid, subject, from_addr, to_addr, cc_addr, body_text)
                VALUES (new.id, new.subject, new.from_addr, new.to_addr, new.cc_addr, new.body_text);
            END;
            CREATE TABLE import_errors (
                source_mailbox TEXT NOT NULL, source_ordinal INTEGER NOT NULL, error TEXT NOT NULL
            );
            """, ct);
        await using var command = database.CreateCommand();
        command.CommandText = "INSERT INTO metadata(key, value) VALUES('format_version', '2'), ('source_directory', $source), ('imported_utc', $now);";
        command.Parameters.AddWithValue("$source", Path.GetFullPath(source));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(SqliteConnection database, string sql, CancellationToken ct)
    {
        await using var command = database.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
