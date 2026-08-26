using System.Diagnostics;
using Microsoft.Data.Sqlite;
using QuickMail.Models;
using QuickMail.Services;

namespace EudoraImporter;

internal static class NativeProfileImportCommand
{
    internal static readonly Guid ImportAccountId = new("ed0da001-0000-4000-8000-000000000001");

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help")) { PrintUsage(); return 0; }
        try
        {
            var source = Path.GetFullPath(ValueAfter(args, "--database") ?? "eudora-messages.db");
            var profilePath = ValueAfter(args, "--profile")
                ?? throw new ArgumentException("Falta --profile; el importador nunca elige un perfil de usuario implícitamente.");
            var profile = new ProfileContext(Path.GetFullPath(profilePath));
            if (!File.Exists(source)) throw new ArgumentException($"No existe la base importada: {source}");

            var rootName = ValueAfter(args, "--root-name") ?? "Eudora";
            var eudoraRoot = ValueAfter(args, "--eudora-root");
            var filtersPath = ValueAfter(args, "--filters");
            var respectCheckMail = string.Equals(ValueAfter(args, "--respect-check-mail"), "true",
                StringComparison.OrdinalIgnoreCase);
            var selective = string.Equals(ValueAfter(args, "--selective"), "true",
                StringComparison.OrdinalIgnoreCase);
            Console.WriteLine("[1/5] Importing Eudora account configuration…");
            var accountImporter = new AccountService(profile);
            var existingAccounts = accountImporter.LoadAccounts();
            var selectiveTarget = existingAccounts.FirstOrDefault(account => account.IsDefault && account.IsActive)
                ?? existingAccounts.FirstOrDefault(account => account.IsActive);
            var importedAccounts = selective && selectiveTarget != null
                ? new EudoraAccountImporter.ImportResult(0, selectiveTarget.Id)
                : string.IsNullOrWhiteSpace(eudoraRoot)
                ? new EudoraAccountImporter.ImportResult(0, Guid.Empty)
                : EudoraAccountImporter.ImportAccounts(Path.Combine(eudoraRoot, "Eudora.ini"),
                    accountImporter, selective ? "Eudora" : rootName, selective ? false : respectCheckMail);
            var targetAccountId = importedAccounts.DominantAccountId;
            if (targetAccountId == Guid.Empty)
                throw new ArgumentException(selective
                    ? "Selective import requires an existing active QuickMail account."
                    : "No se pudo localizar la cuenta Dominant en [Settings] de Eudora.ini.");
            var dominantAddress = accountImporter.LoadAccounts()
                .FirstOrDefault(account => account.Id == targetAccountId)?.Username ?? string.Empty;
            if (!selective) ApplyImportedDefaults(profile, targetAccountId);

            var stopwatch = Stopwatch.StartNew();
            Console.WriteLine("[2/5] Preparing the local message database…");
            var store = new LocalStoreService(profile);
            store.Initialize();
            Console.WriteLine("[3/5] Creating the Eudora folder tree…");
            var folders = await ReadFolderLayoutAsync(source, targetAccountId);
            var cached = await store.LoadFoldersAsync();
            var merged = cached.GetValueOrDefault(targetAccountId, [])
                .Where(existing => !folders.Any(imported => imported.FullName.Equals(existing.FullName, StringComparison.OrdinalIgnoreCase)))
                .Concat(folders).ToList();
            await store.SaveFoldersAsync(targetAccountId, merged);
            if (!selective && targetAccountId != ImportAccountId)
            {
                await store.DeleteAccountDataAsync(ImportAccountId);
                RemoveLegacyImportAccount(profile);
            }
            Console.WriteLine("[4/5] Importing messages and building the full-text search index.");
            Console.WriteLine("      This is the longest stage. Do not close this window.");
            var count = await RunWithHeartbeatAsync(
                () => BulkCopyMessagesAsync(source, Path.Combine(profile.ProfileDir, "mail.db"), folders,
                    targetAccountId, dominantAddress, selective),
                "Still importing messages and building the search index");
            stopwatch.Stop();
            Console.WriteLine("[4/5] Message import and search indexing completed.");
            Console.WriteLine("[5/5] Importing Eudora filters…");
            EudoraFilterImporter.Result? filterResult = null;
            if (!string.IsNullOrWhiteSpace(filtersPath))
                filterResult = EudoraFilterImporter.Import(filtersPath, profile.ProfileDir, folders);
            else
                Console.WriteLine("      Filter import was disabled.");
            Console.WriteLine($"Perfil: {profile.ProfileDir}");
            Console.WriteLine($"Cuenta Dominant: {targetAccountId}");
            Console.WriteLine($"Passwords protected and imported: {importedAccounts.PasswordsImported:N0}");
            Console.WriteLine($"Carpetas: {folders.Count:N0}");
            Console.WriteLine($"Mensajes: {count:N0}");
            if (filterResult is not null)
                Console.WriteLine($"Filtros: {filterResult.Imported:N0} imported, " +
                    $"{filterResult.Existing:N0} already present, {filterResult.Unsupported:N0} unsupported " +
                    $"(of {filterResult.Total:N0})");
            Console.WriteLine($"Tiempo: {stopwatch.Elapsed:c}");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or SqliteException or IOException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static async Task<T> RunWithHeartbeatAsync<T>(Func<Task<T>> operation, string activity)
    {
        using var stopped = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var heartbeat = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stopped.Token);
                    Console.WriteLine($"      {activity}… elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}");
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested)
            {
            }
        });

        try
        {
            return await operation();
        }
        finally
        {
            stopped.Cancel();
            await heartbeat;
        }
    }

    private static void RemoveLegacyImportAccount(ProfileContext profile)
    {
        var accounts = new AccountService(profile);
        var all = accounts.LoadAccounts();
        all.RemoveAll(a => a.Id == ImportAccountId);
        accounts.SaveAccounts(all);
    }

    private static void ApplyImportedDefaults(ProfileContext profile, Guid dominantAccountId)
    {
        var service = new ConfigService(profile);
        var config = service.Load();
        config.ShowAccountsPanel = false;
        config.ShowCombinedViews = false;
        config.ShowTodayAgenda = true;
        config.ShowCalendar = true;
        config.NotifyOnNewMail = true;
        config.AutoSaveDrafts = true;
        config.AutoSaveIntervalSeconds = 30;
        config.DefaultComposeMode = ComposeMode.Html;
        config.StartupFolder = "In";
        config.StartupFolderAccount = dominantAccountId.ToString();
        config.StartupFolderLabel = "In";
        service.Save(config);
    }

    private static async Task<List<MailFolderModel>> ReadFolderLayoutAsync(string source, Guid accountId)
    {
        await using var connection = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False;");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT mailbox FROM messages ORDER BY mailbox;";
        var mailboxes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) mailboxes.Add(reader.GetString(0));

        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mailbox in mailboxes)
        {
            var parts = mailbox.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i <= parts.Length; i++)
            {
                var path = string.Join('/', parts.Take(i));
                allPaths.Add(path);
                if (i < parts.Length) parents.Add(path);
            }
        }

        // If Eudora has both Foo.mbx and Foo.fol, Foo becomes a virtual container and its own
        // messages live in an explicit child. No message can ever be assigned to the container.
        foreach (var mailbox in mailboxes.Where(parents.Contains)) allPaths.Add(mailbox + "/Messages");
        return allPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(path => new MailFolderModel
        {
            AccountId = accountId,
            FullName = path,
            DisplayName = path.Split('/')[^1],
            ParentId = path.Contains('/') ? path[..path.LastIndexOf('/')] : null,
            IsContainer = parents.Contains(path),
            Kind = SpecialKind(path),
            ExcludeFromAllMail = SpecialKind(path) is SpecialFolderKind.Trash or SpecialFolderKind.Sent,
        }).ToList();
    }

    private static SpecialFolderKind SpecialKind(string path) => path.ToLowerInvariant() switch
    {
        "in" or "inbox" => SpecialFolderKind.Inbox,
        "out" or "sent" => SpecialFolderKind.Sent,
        "trash" => SpecialFolderKind.Trash,
        "junk" => SpecialFolderKind.Junk,
        _ => SpecialFolderKind.None,
    };

    private static async Task<long> BulkCopyMessagesAsync(string source, string target,
        IReadOnlyList<MailFolderModel> folders, Guid accountId, string dominantAddress, bool selective)
    {
        await using var connection = new SqliteConnection($"Data Source={target};Mode=ReadWrite;Pooling=False;");
        await connection.OpenAsync();
        connection.CreateFunction<string?, long>("dotnet_ticks", value =>
            DateTimeOffset.TryParse(value, out var parsed) ? parsed.UtcTicks : DateTimeOffset.MinValue.UtcTicks);
        connection.CreateFunction<string?, string?, string?, string>("import_recipient", (value, mailbox, fallback) =>
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
            var leaf = (mailbox ?? string.Empty).Replace('\\', '/').Split('/')[^1]
                .Trim().TrimStart('_').ToLowerInvariant();
            // A missing recipient on received/archived mail generally means BCC or a stripped To
            // header. Never invent the sender's own address for an outgoing message.
            return leaf is "out" or "sent" ? string.Empty : fallback ?? string.Empty;
        });
        connection.CreateFunction<string?, long>("import_direction", mailbox =>
        {
            var segments = (mailbox ?? string.Empty).Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return segments.Any(segment => segment.TrimStart('_').Equals("out", StringComparison.OrdinalIgnoreCase)
                                           || segment.Equals("sent", StringComparison.OrdinalIgnoreCase)
                                           || segment.Equals("sent items", StringComparison.OrdinalIgnoreCase))
                ? (long)MessageDirection.Outgoing
                : (long)MessageDirection.Incoming;
        });
        await using var attach = connection.CreateCommand();
        attach.CommandText = "ATTACH DATABASE $source AS eudora;";
        attach.Parameters.AddWithValue("$source", source);
        await attach.ExecuteNonQueryAsync();

        await using var tx = await connection.BeginTransactionAsync();
        await using (var map = connection.CreateCommand())
        {
            map.Transaction = (SqliteTransaction)tx;
            map.CommandText = "CREATE TEMP TABLE FolderMap(original TEXT PRIMARY KEY, target TEXT NOT NULL);";
            await map.ExecuteNonQueryAsync();
            var containers = folders.Where(f => f.IsContainer).Select(f => f.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            await using var insertMap = connection.CreateCommand();
            insertMap.Transaction = (SqliteTransaction)tx;
            insertMap.CommandText = "INSERT INTO FolderMap VALUES($original,$target);";
            var original = insertMap.Parameters.Add("$original", SqliteType.Text);
            var destination = insertMap.Parameters.Add("$target", SqliteType.Text);
            await using var sourceFolders = connection.CreateCommand();
            sourceFolders.Transaction = (SqliteTransaction)tx;
            sourceFolders.CommandText = "SELECT DISTINCT mailbox FROM eudora.messages;";
            await using var folderReader = await sourceFolders.ExecuteReaderAsync();
            while (await folderReader.ReadAsync())
            {
                var name = folderReader.GetString(0);
                original.Value = name;
                destination.Value = containers.Contains(name) ? name + "/Messages" : name;
                await insertMap.ExecuteNonQueryAsync();
            }
        }

        await using var copy = connection.CreateCommand();
        copy.Transaction = (SqliteTransaction)tx;
        var importedId = selective
            ? "'eudora-selective-'||lower(hex(m.source_mailbox))||'-'||m.source_ordinal"
            : "'eudora-'||m.id";
        var cleanup = selective ? """
            CREATE TEMP TABLE IncomingEudoraIds(unique_id TEXT PRIMARY KEY) WITHOUT ROWID;
            INSERT INTO IncomingEudoraIds
                SELECT 'eudora-selective-'||lower(hex(source_mailbox))||'-'||source_ordinal FROM eudora.messages;
            DELETE FROM LocalMessageFts WHERE rowid IN (
                SELECT fts_rowid FROM LocalMessageFtsKey WHERE account_id=$aid
                AND unique_id IN (SELECT unique_id FROM IncomingEudoraIds));
            DELETE FROM LocalMessageFtsKey WHERE account_id=$aid
                AND unique_id IN (SELECT unique_id FROM IncomingEudoraIds);
            DELETE FROM MessageDetail WHERE account_id=$aid
                AND unique_id IN (SELECT unique_id FROM IncomingEudoraIds);
            DELETE FROM AttachmentContent WHERE account_id=$aid
                AND unique_id IN (SELECT unique_id FROM IncomingEudoraIds);
            DELETE FROM MessageSummary WHERE account_id=$aid
                AND unique_id IN (SELECT unique_id FROM IncomingEudoraIds);
            """ : """
            DELETE FROM LocalMessageFts WHERE account_id=$aid AND unique_id LIKE 'eudora-%';
            DELETE FROM LocalMessageFtsKey WHERE account_id=$aid AND unique_id LIKE 'eudora-%';
            DELETE FROM MessageDetail WHERE account_id=$aid AND unique_id LIKE 'eudora-%';
            DELETE FROM AttachmentContent WHERE account_id=$aid AND unique_id LIKE 'eudora-%';
            DELETE FROM MessageSummary WHERE account_id=$aid AND unique_id LIKE 'eudora-%';
            """;
        var keyScope = selective
            ? "AND unique_id IN (SELECT unique_id FROM IncomingEudoraIds)"
            : string.Empty;
        copy.CommandText = cleanup + $$"""

            INSERT INTO MessageSummary
                (unique_id,account_id,folder_name,from_disp,to_addr,subject,date_ticks,is_read,
                 preview_text,is_replied,is_forwarded,has_attachments,is_mailing_list,flag_id,internet_message_id,message_direction)
            SELECT {{importedId}},$aid,f.target,m.from_addr,import_recipient(m.to_addr,m.mailbox,$recipient),m.subject,dotnet_ticks(m.date_utc),m.is_read,
                   substr(replace(replace(m.body_text,char(13),' '),char(10),' '),1,240),0,0,
                   CASE WHEN m.attachments_json IS NULL THEN 0 ELSE 1 END,0,NULL,m.message_id,import_direction(m.mailbox)
            FROM eudora.messages m JOIN FolderMap f ON f.original=m.mailbox;

            INSERT INTO MessageDetail
                (unique_id,account_id,folder_name,to_addr,cc,reply_to,plain_body,html_body,attachments_json,calendar_ics,raw_headers)
            SELECT {{importedId}},$aid,f.target,import_recipient(m.to_addr,m.mailbox,$recipient),m.cc_addr,'',m.body_text,m.body_html,m.attachments_json,NULL,m.raw_headers
            FROM eudora.messages m JOIN FolderMap f ON f.original=m.mailbox;

            INSERT INTO LocalMessageFts
                (account_id,unique_id,folder_name,from_addr,to_addr,cc_addr,subject,body_text)
            SELECT $aid,{{importedId}},f.target,m.from_addr,import_recipient(m.to_addr,m.mailbox,$recipient),m.cc_addr,m.subject,m.body_text
            FROM eudora.messages m JOIN FolderMap f ON f.original=m.mailbox;

            INSERT INTO LocalMessageFtsKey(account_id,unique_id,folder_name,fts_rowid)
            SELECT account_id,unique_id,folder_name,rowid FROM LocalMessageFts
            WHERE account_id=$aid {{keyScope}};

            -- Persist counts during the import so the first QuickMail launch has the same tree
            -- badges as subsequent launches; previously they appeared only after a reconnect.
            DROP TABLE IF EXISTS temp.ImportedFolderCounts;
            CREATE TEMP TABLE ImportedFolderCounts(
                folder_name TEXT PRIMARY KEY,
                message_count INTEGER NOT NULL,
                unread_count INTEGER NOT NULL
            ) WITHOUT ROWID;
            INSERT INTO ImportedFolderCounts(folder_name,message_count,unread_count)
            SELECT folder_name,COUNT(*),COALESCE(SUM(CASE WHEN is_read=0 THEN 1 ELSE 0 END),0)
            FROM MessageSummary WHERE account_id=$aid GROUP BY folder_name;
            UPDATE Folder SET
                message_count=COALESCE((SELECT message_count FROM ImportedFolderCounts c
                                       WHERE c.folder_name=Folder.full_name),0),
                unread_count=COALESCE((SELECT unread_count FROM ImportedFolderCounts c
                                      WHERE c.folder_name=Folder.full_name),0)
            WHERE account_id=$aid;
            DROP TABLE ImportedFolderCounts;
            {{(selective ? "DROP TABLE IncomingEudoraIds;" : string.Empty)}}
            """;
        copy.Parameters.AddWithValue("$aid", accountId.ToString());
        copy.Parameters.AddWithValue("$recipient", dominantAddress);
        await copy.ExecuteNonQueryAsync();
        await tx.CommitAsync();

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM MessageSummary WHERE account_id=$aid;";
        count.Parameters.AddWithValue("$aid", accountId.ToString());
        return Convert.ToInt64(await count.ExecuteScalarAsync() ?? 0);
    }

    private static string? ValueAfter(string[] args, string option)
    {
        for (var i = 0; i < args.Length; i++)
            if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"Falta el valor de {option}.");
        return null;
    }

    private static void PrintUsage() => Console.WriteLine(
        "dotnet run --project Tools/EudoraImporter -c Release -- native-import --profile <directorio> [--database eudora-messages.db]");
}
