using System.IO;
using Microsoft.Data.Sqlite;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Tests;

public sealed class LocalFirstMailTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "quickmail-local-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DpapiSecrets_RoundTripForCurrentWindowsUser()
    {
        var protector = new DpapiAccountSecretProtector();
        var encrypted = protector.Protect("not-plain-text");
        Assert.DoesNotContain("not-plain-text", encrypted);
        Assert.Equal("not-plain-text", protector.Unprotect(encrypted));
        Assert.Null(protector.Unprotect("not base64"));
    }

    [Fact]
    public async Task LocalStore_IndexesMovesAndDeletesAuthoritativeMessage()
    {
        var accountId = Guid.NewGuid();
        var store = CreateStore();
        await store.SaveFoldersAsync(accountId,
        [
            Folder(accountId, "Inbox", container: false),
            Folder(accountId, "Clients", container: true),
            Folder(accountId, "Clients/Acme", container: false),
        ]);
        var message = Message(accountId, "Inbox", "m1", "DeporWin invoice body");
        await store.SavePop3MessageAsync("uid-1", message);

        Assert.True(await store.ContainsPop3UidAsync(accountId, "uid-1"));
        var found = await store.SearchLocalMessagesAsync(new LocalSearchQuery("DeporWin", accountId));
        Assert.Equal(1, found.TotalMatches);
        Assert.Single(found.Messages);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MoveLocalMessagesAsync(accountId, "Inbox", "Clients", ["m1"]));
        await store.MoveLocalMessagesAsync(accountId, "Inbox", "Clients/Acme", ["m1"]);
        Assert.Empty((await store.SearchLocalMessagesAsync(
            new LocalSearchQuery("DeporWin", accountId, "Inbox"))).Messages);
        Assert.Single((await store.SearchLocalMessagesAsync(
            new LocalSearchQuery("DeporWin", accountId, "Clients/Acme"))).Messages);

        await store.DeleteLocalMessagesAsync(accountId, "Clients/Acme", ["m1"]);
        Assert.Equal(0, (await store.SearchLocalMessagesAsync(new LocalSearchQuery("DeporWin", accountId))).TotalMatches);
        Assert.True(await store.ContainsPop3UidAsync(accountId, "uid-1"));
    }

    [Fact]
    public async Task LocalStore_SenderRepairPreservesRecipientSubjectAndDate()
    {
        var accountId = Guid.NewGuid();
        var store = CreateStore();
        var original = Message(accountId, "In", "sender-repair", "body");
        var originalDate = original.Date;
        await store.SaveLocalMessageAsync(original);

        await store.UpdateSenderAsync(accountId, "In", original.MessageId,
            "Support <support@example.test>");

        var repaired = Assert.Single(await store.LoadFolderSummariesAsync(accountId, "In"));
        Assert.Equal("Support <support@example.test>", repaired.From);
        Assert.Equal(original.To, repaired.To);
        Assert.Equal(original.Subject, repaired.Subject);
        Assert.Equal(originalDate, repaired.Date);
    }

    [Fact]
    public async Task LocalStore_EnforcesRenderedMessageCap()
    {
        var accountId = Guid.NewGuid();
        var store = CreateStore();
        for (var i = 0; i < LocalMailConstants.MaxRenderedMessages + 5; i++)
            await store.SaveLocalMessageAsync(Message(accountId, "Inbox", $"m{i}", "commonprefix"));

        var found = await store.SearchLocalMessagesAsync(new LocalSearchQuery("commonprefix", Limit: 50_000));
        Assert.Equal(LocalMailConstants.MaxRenderedMessages + 5, found.TotalMatches);
        Assert.Equal(LocalMailConstants.MaxRenderedMessages, found.Messages.Count);

        var secondPage = await store.LoadLocalPageAsync(accountId, "Inbox",
            LocalMailConstants.MaxRenderedMessages, LocalMailConstants.MaxRenderedMessages);
        Assert.Equal(LocalMailConstants.MaxRenderedMessages + 5, secondPage.TotalMatches);
        Assert.Equal(5, secondPage.Messages.Count);
    }

    [Fact]
    public async Task LocalSearch_FolderScopes_RestrictUnifiedInboxToItsPhysicalFolders()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var store = CreateStore();
        await store.SaveLocalMessageAsync(Message(first, "In", "in-1", "Parisi"));
        await store.SaveLocalMessageAsync(Message(first, "Archive", "archive-1", "Parisi"));
        await store.SaveLocalMessageAsync(Message(second, "Inbox", "inbox-2", "Parisi"));

        var result = await store.SearchLocalMessagesAsync(new LocalSearchQuery("Parisi",
            FolderScopes:
            [
                new LocalFolderScope(first, "In"),
                new LocalFolderScope(second, "Inbox"),
            ]));

        Assert.Equal(2, result.TotalMatches);
        Assert.DoesNotContain(result.Messages, message => message.FolderName == "Archive");
    }

    [Fact]
    public async Task LocalSearch_FlagCommand_UsesTheSelectedPhysicalFolderScope()
    {
        var account = Guid.NewGuid();
        var store = CreateStore();
        await store.SaveLocalMessageAsync(Message(account, "In", "flagged-in", "first"));
        await store.SaveLocalMessageAsync(Message(account, "Archive", "flagged-archive", "second"));
        await store.SaveLocalMessageAsync(Message(account, "In", "plain-in", "third"));
        await store.UpdateFlagIdAsync(account, "In", "flagged-in", "important");
        await store.UpdateFlagIdAsync(account, "Archive", "flagged-archive", "important");

        var result = await store.SearchLocalMessagesAsync(new LocalSearchQuery("F",
            FolderScopes: [new LocalFolderScope(account, "In")]));

        Assert.Equal(1, result.TotalMatches);
        Assert.Equal("flagged-in", Assert.Single(result.Messages).MessageId);
    }

    [Fact]
    public async Task LocalPage_KnownTotalAvoidsRecountButStillLoadsTheRequestedRows()
    {
        var account = Guid.NewGuid();
        var store = CreateStore();
        await store.SaveLocalMessageAsync(Message(account, "In", "one", "body"));

        var result = await store.LoadLocalPageAsync(account, "In", 20, 0, knownTotal: 123);

        Assert.Equal(123, result.TotalMatches);
        Assert.Equal("one", Assert.Single(result.Messages).MessageId);
    }

    [Fact]
    public async Task CanonicalTree_LoadsTransactionallyMaintainedFolderCounts()
    {
        var account = Guid.NewGuid();
        var root = Guid.NewGuid();
        var store = CreateStore();
        await store.SaveFoldersAsync(account, [Folder(account, "In", container: false)]);
        await store.SaveLocalMessageAsync(Message(account, "In", "one", "body"));
        new LocalFolderTreeMigrationService().BuildShadowTree(Path.Combine(_directory, "mail.db"),
        [
            new AccountModel
            {
                Id = account, FolderTreeRootId = root, FolderTreeRootName = "Eudora",
                IsActive = true, BackendKind = BackendKind.Pop3Smtp,
            },
        ]);

        var tree = await store.LoadCanonicalLocalFolderTreeAsync();
        Assert.NotNull(tree);
        var inbox = Assert.Single(tree!.Folders.Where(folder => folder.CanonicalPath == "In"));

        Assert.Equal(1, inbox.MessageCount);
        Assert.Equal(1, inbox.UnreadCount);
    }

    [Fact]
    public async Task CanonicalIn_IncludesRootlessImapInbox_WhenThereIsOnlyOneVisibleRoot()
    {
        var localId = Guid.NewGuid();
        var imapId = Guid.NewGuid();
        var rootId = localId;
        var local = new AccountModel
        {
            Id = localId, AccountName = "Local", Username = "local@example.test",
            BackendKind = BackendKind.Pop3Smtp, IsActive = true,
            FolderTreeRootName = "Eudora",
        };
        var imap = new AccountModel
        {
            Id = imapId, AccountName = "Gmail", Username = "gmail@example.test",
            BackendKind = BackendKind.ImapSmtp, IsActive = true,
            // Deliberately no FolderTreeRootId: this is the production upgrade shape.
        };
        var store = CreateStore();
        await store.SaveFoldersAsync(localId,
        [
            new MailFolderModel { AccountId = localId, FullName = "In", DisplayName = "In", Kind = SpecialFolderKind.Inbox },
        ]);
        await store.SaveFoldersAsync(imapId,
        [
            // Older restored caches did not always persist the special-use kind. The conventional
            // IMAP name must still participate in the unified In folder and Alt+cell searches.
            new MailFolderModel { AccountId = imapId, FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.None },
        ]);
        new LocalFolderTreeMigrationService().BuildShadowTree(Path.Combine(_directory, "mail.db"), [local]);

        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());
        vm.LoadAccountList([local, imap]);
        await vm.InitialLoadAsync();

        var shortcut = vm.ResolveInboxShortcutFolder();
        var sources = vm.FolderScopedAggregateSources(shortcut.FullName).ToList();

        Assert.Equal("In", shortcut.DisplayName);
        Assert.Contains(sources, source => source.Account.Id == localId && source.Folder.FullName == "In");
        Assert.Contains(sources, source => source.Account.Id == imapId && source.Folder.FullName == "INBOX");
        vm.Dispose();
    }

    [Fact]
    public async Task LocalSystemFolders_AreCreatedWithoutConnectingThePop3Account()
    {
        var accountId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var store = CreateStore();
        var account = new AccountModel
        {
            Id = accountId,
            AccountName = "Offline POP",
            Username = "offline@example.test",
            BackendKind = BackendKind.Pop3Smtp,
            FolderTreeRootId = rootId,
            FolderTreeRootName = "Local mail",
            IsActive = true,
        };

        await store.EnsureLocalSystemFoldersAsync([account]);

        var physical = (await store.LoadFoldersAsync())[accountId];
        Assert.Equal(6, physical.Count);
        Assert.Equal(
            new HashSet<SpecialFolderKind>
            {
                SpecialFolderKind.Inbox, SpecialFolderKind.Drafts, SpecialFolderKind.Scheduled,
                SpecialFolderKind.Sent, SpecialFolderKind.Trash, SpecialFolderKind.Junk,
            },
            physical.Select(folder => folder.Kind).ToHashSet());
        Assert.All(physical.Where(folder => folder.Kind is SpecialFolderKind.Drafts or SpecialFolderKind.Scheduled),
            folder => Assert.True(folder.ExcludeFromAllMail));
        Assert.All(physical.Where(folder => folder.Kind is SpecialFolderKind.Trash or SpecialFolderKind.Junk),
            folder => Assert.False(folder.ExcludeFromAllMail));

        var canonical = await store.LoadCanonicalLocalFolderTreeAsync();
        Assert.NotNull(canonical);
        Assert.Equal(6, canonical!.Folders.Count(folder => folder.ParentFolderId != null));
        Assert.All(canonical.Folders.Where(folder => folder.ParentFolderId != null),
            folder => Assert.Contains(folder.Bindings, binding => binding.AccountId == accountId));
    }

    [Fact]
    public async Task DraftAndScheduledSave_CreateTheirPhysicalAndCanonicalSystemFoldersOnDemand()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "POP", Username = "sender@example.test",
            BackendKind = BackendKind.Pop3Smtp, FolderTreeRootId = Guid.NewGuid(),
            FolderTreeRootName = "Local mail", IsActive = true,
        };
        var store = CreateStore();
        var profile = new ProfileContext(_directory);
        var accounts = new AccountService(profile);
        accounts.SaveAccounts([account]);
        var compose = new ComposeModel
        {
            AccountId = account.Id, To = "recipient@example.test", Subject = "test",
            Body = "body", Mode = ComposeMode.Html, HtmlBody = "<p>body</p>",
        };

        var localMail = new LocalMailService(store, accounts);
        await localMail.AppendDraftAsync(account.Id, compose, null);
        using (var scheduled = new ScheduledSendService(
                   profile, new StubSmtpService(), accounts, new StubCredentialService(), store, localMail))
            await scheduled.ScheduleAsync(compose, DateTimeOffset.Now.AddHours(1));

        var physical = (await store.LoadFoldersAsync())[account.Id];
        Assert.Contains(physical, folder => folder.Kind == SpecialFolderKind.Drafts && folder.ExcludeFromAllMail);
        Assert.Contains(physical, folder => folder.Kind == SpecialFolderKind.Scheduled && folder.ExcludeFromAllMail);
        Assert.Equal(1, (await store.LoadLocalPageAsync(account.Id, "Draft", 10, 0)).TotalMatches);
        Assert.Equal(1, (await store.LoadLocalPageAsync(account.Id, "Scheduled", 10, 0)).TotalMatches);

        var canonical = await store.LoadCanonicalLocalFolderTreeAsync();
        Assert.Contains(canonical!.Folders, folder => folder.Kind == SpecialFolderKind.Drafts &&
            folder.Bindings.Any(binding => binding.AccountId == account.Id));
        Assert.Contains(canonical.Folders, folder => folder.Kind == SpecialFolderKind.Scheduled &&
            folder.Bindings.Any(binding => binding.AccountId == account.Id));
    }

    [Fact]
    public async Task ScheduledSend_WhenOffline_RemainsScheduledWithErrorAndNeverBecomesDraft()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "POP", Username = "sender@example.test",
            BackendKind = BackendKind.Pop3Smtp, FolderTreeRootId = Guid.NewGuid(),
            FolderTreeRootName = "Local mail", IsActive = true,
        };
        var store = CreateStore();
        var profile = new ProfileContext(_directory);
        var accounts = new AccountService(profile);
        accounts.SaveAccounts([account]);
        var smtp = new StubSmtpService { SendFailure = new IOException("network unavailable") };
        var localMail = new LocalMailService(store, accounts);
        using var scheduled = new ScheduledSendService(
            profile, smtp, accounts, new StubCredentialService(), store, localMail);

        await scheduled.ScheduleAsync(new ComposeModel
        {
            AccountId = account.Id, To = "recipient@example.test", Subject = "offline",
            Body = "body", Mode = ComposeMode.Html, HtmlBody = "<p>body</p>",
        }, DateTimeOffset.Now.AddMilliseconds(25));
        await Task.Delay(75);

        var failures = await scheduled.DispatchDueAsync(manual: true);

        var queued = Assert.Single(await scheduled.GetSnapshotAsync());
        Assert.Equal(1, queued.Attempts);
        Assert.Contains("network unavailable", queued.LastError);
        Assert.Single(failures);
        Assert.Equal(1, (await store.LoadLocalPageAsync(account.Id, "Scheduled", 10, 0)).TotalMatches);
        Assert.Equal(0, (await store.LoadLocalPageAsync(account.Id, "Draft", 10, 0)).TotalMatches);
        Assert.Equal(0, (await store.LoadLocalPageAsync(account.Id, "Sent", 10, 0)).TotalMatches);
    }

    [Fact]
    public async Task ScheduledSend_AfterSmtpAcceptance_MovesLocalCopyFromScheduledToSent()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "POP", Username = "sender@example.test",
            BackendKind = BackendKind.Pop3Smtp, FolderTreeRootId = Guid.NewGuid(),
            FolderTreeRootName = "Local mail", IsActive = true,
        };
        var store = CreateStore();
        var profile = new ProfileContext(_directory);
        var accounts = new AccountService(profile);
        accounts.SaveAccounts([account]);
        var localMail = new LocalMailService(store, accounts);
        using var scheduled = new ScheduledSendService(
            profile, new StubSmtpService(), accounts, new StubCredentialService(), store, localMail);
        Guid? changedAccount = null;
        scheduled.SentMailChanged += id => changedAccount = id;

        await scheduled.ScheduleAsync(new ComposeModel
        {
            AccountId = account.Id, To = "recipient@example.test", Subject = "scheduled",
            Body = "body", Mode = ComposeMode.Html, HtmlBody = "<p>body</p>",
        }, DateTimeOffset.Now.AddMilliseconds(25));
        await Task.Delay(75);

        var failures = await scheduled.DispatchDueAsync(manual: true);

        Assert.Empty(failures);
        Assert.Empty(await scheduled.GetSnapshotAsync());
        Assert.Equal(account.Id, changedAccount);
        Assert.Equal(0, (await store.LoadLocalPageAsync(account.Id, "Scheduled", 10, 0)).TotalMatches);
        var sent = await store.LoadLocalPageAsync(account.Id, "Sent", 10, 0);
        Assert.Equal(1, sent.TotalMatches);
        Assert.Equal(MessageDirection.Outgoing, Assert.Single(sent.Messages).Direction);
    }

    [Fact]
    public async Task CanonicalFolderRename_UpdatesDescendantsMessagesAndDisplayName()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "POP", Username = "sender@example.test",
            BackendKind = BackendKind.Pop3Smtp, FolderTreeRootId = Guid.NewGuid(),
            FolderTreeRootName = "Local mail", IsActive = true,
        };
        var store = CreateStore();
        await store.EnsureLocalSystemFoldersAsync([account]);
        var root = (await store.LoadCanonicalLocalFolderTreeAsync())!.Folders.Single(folder => folder.ParentFolderId == null);
        var parentId = await store.CreateCanonicalFolderAsync(root.FolderId, "Customers", account.Id, true);
        var childId = await store.CreateCanonicalFolderAsync(parentId, "Acme", account.Id, false);
        await store.SaveLocalMessageAsync(Message(account.Id, "Customers/Acme", "message", "body"));

        var result = await store.RenameCanonicalFolderAsync(parentId, "Clients");

        Assert.Equal("Customers", result.OldPath);
        Assert.Equal("Clients", result.NewPath);
        Assert.Equal(1, (await store.LoadLocalPageAsync(account.Id, "Clients/Acme", 10, 0)).TotalMatches);
        var tree = await store.LoadCanonicalLocalFolderTreeAsync();
        Assert.Contains(tree!.Folders, folder => folder.FolderId == parentId &&
            folder.Name == "Clients" && folder.CanonicalPath == "Clients");
        Assert.Contains(tree.Folders, folder => folder.FolderId == childId && folder.CanonicalPath == "Clients/Acme");
        Assert.Contains((await store.LoadFoldersAsync())[account.Id], folder =>
            folder.FullName == "Clients" && folder.DisplayName == "Clients");
    }

    [Fact]
    public async Task CanonicalFolder_ExistingEmptyContainerCanBeFinishedAsMessageFolder()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "POP", Username = "sender@example.test",
            BackendKind = BackendKind.Pop3Smtp, FolderTreeRootId = Guid.NewGuid(),
            FolderTreeRootName = "Local mail", IsActive = true,
        };
        var store = CreateStore();
        await store.EnsureLocalSystemFoldersAsync([account]);
        var root = (await store.LoadCanonicalLocalFolderTreeAsync())!.Folders
            .Single(folder => folder.ParentFolderId == null);
        var parent = await store.CreateCanonicalFolderAsync(root.FolderId, "Prov", account.Id, true);
        var miele = await store.CreateCanonicalFolderAsync(parent, "Miele", account.Id, true);

        // Simulates retrying an interrupted/legacy quick-drop creation. Both the canonical node and
        // its already-existing physical binding must become leaves, otherwise the move validator
        // still rejects the message even though the tree displays a valid empty folder.
        Assert.Equal(miele, await store.CreateCanonicalFolderAsync(parent, "Miele", account.Id, false));
        await store.SaveLocalMessageAsync(Message(account.Id, "In", "miele-message", "body"));
        var binding = await store.EnsureCanonicalFolderBindingAsync(miele, account.Id);

        await store.MoveLocalMessagesAsync(account.Id, "In", binding, ["miele-message"]);

        Assert.Equal(1, (await store.LoadLocalPageAsync(account.Id, binding, 10, 0)).TotalMatches);
        Assert.False((await store.LoadFoldersAsync())[account.Id]
            .Single(folder => folder.FullName == binding).IsContainer);
    }

    [Fact]
    public async Task CanonicalFolderRename_PreservesImportedPhysicalPathPrefix()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "POP", Username = "sender@example.test",
            BackendKind = BackendKind.Pop3Smtp, FolderTreeRootId = Guid.NewGuid(),
            FolderTreeRootName = "Local mail", IsActive = true,
        };
        var store = CreateStore();
        await store.EnsureLocalSystemFoldersAsync([account]);
        var root = (await store.LoadCanonicalLocalFolderTreeAsync())!.Folders.Single(folder => folder.ParentFolderId == null);
        var parent = await store.CreateCanonicalFolderAsync(root.FolderId, "Varios", account.Id, true);
        var ocio = await store.CreateCanonicalFolderAsync(parent, "Ocio", account.Id, false);
        await store.RenameFolderPathAsync(account.Id, "Varios/Ocio", "Imported/Varios/Ocio");
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "mail.db")}"))
        {
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE LocalFolderBinding_shadow SET legacy_full_name='Imported/Varios/Ocio'
                 WHERE folder_id=$folder AND account_id=$account;
                """;
            update.Parameters.AddWithValue("$folder", ocio.ToString("D"));
            update.Parameters.AddWithValue("$account", account.Id.ToString("D"));
            update.ExecuteNonQuery();
        }
        await store.SaveLocalMessageAsync(Message(account.Id, "Imported/Varios/Ocio", "message", "body"));

        await store.RenameCanonicalFolderAsync(ocio, "Pintura");

        Assert.Equal(1, (await store.LoadLocalPageAsync(
            account.Id, "Imported/Varios/Pintura", 10, 0)).TotalMatches);
        var tree = await store.LoadCanonicalLocalFolderTreeAsync();
        var renamed = tree!.Folders.Single(folder => folder.FolderId == ocio);
        Assert.Equal("Varios/Pintura", renamed.CanonicalPath);
        Assert.Contains(renamed.Bindings, binding =>
            binding.LegacyFullName == "Imported/Varios/Pintura");
    }

    [Fact]
    public async Task CanonicalFolderMove_IntoHomonymousFolder_MergesMessagesAndRemovesSourceNode()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "POP", Username = "sender@example.test",
            BackendKind = BackendKind.Pop3Smtp, FolderTreeRootId = Guid.NewGuid(),
            FolderTreeRootName = "Local mail", IsActive = true,
        };
        var store = CreateStore();
        await store.EnsureLocalSystemFoldersAsync([account]);
        var root = (await store.LoadCanonicalLocalFolderTreeAsync())!.Folders.Single(folder => folder.ParentFolderId == null);
        var sourceParent = await store.CreateCanonicalFolderAsync(root.FolderId, "Varios", account.Id, true);
        var source = await store.CreateCanonicalFolderAsync(sourceParent, "Ocio", account.Id, false);
        var destinationParent = await store.CreateCanonicalFolderAsync(root.FolderId, "Archive", account.Id, true);
        var destination = await store.CreateCanonicalFolderAsync(destinationParent, "Ocio", account.Id, false);
        await store.SaveLocalMessageAsync(Message(account.Id, "Varios/Ocio", "source", "source body"));
        await store.SaveLocalMessageAsync(Message(account.Id, "Archive/Ocio", "destination", "destination body"));

        var result = await store.MoveCanonicalFolderAsync(source, destinationParent);

        Assert.True(result.WasMerged);
        Assert.Equal("Varios/Ocio", result.OldPath);
        Assert.Equal("Archive/Ocio", result.NewPath);
        var page = await store.LoadLocalPageAsync(account.Id, "Archive/Ocio", 10, 0);
        Assert.Equal(2, page.TotalMatches);
        var tree = await store.LoadCanonicalLocalFolderTreeAsync();
        Assert.DoesNotContain(tree!.Folders, folder => folder.FolderId == source);
        Assert.Contains(tree.Folders, folder => folder.FolderId == destination &&
            folder.Bindings.Any(binding => binding.AccountId == account.Id &&
                binding.LegacyFullName == "Archive/Ocio"));
    }

    [Fact]
    public void LocalStore_CreatesIndexesForEverySqlSortableMessageColumn()
    {
        var store = CreateStore();
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "mail.db")};Mode=ReadOnly;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name LIKE 'idx_summary_%';";
        using var reader = command.ExecuteReader();
        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) indexes.Add(reader.GetString(0));

        Assert.Contains("idx_summary_date", indexes);
        Assert.Contains("idx_summary_from_date", indexes);
        Assert.Contains("idx_summary_to_date", indexes);
        Assert.Contains("idx_summary_subject_date", indexes);
        Assert.Contains("idx_summary_read_date", indexes);
        Assert.Contains("idx_summary_attachments_date", indexes);
        Assert.Contains("idx_summary_account_folder_from_date", indexes);
        Assert.Contains("idx_summary_account_folder_to_date", indexes);
        Assert.Contains("idx_summary_account_folder_subject_date", indexes);
        Assert.Contains("idx_summary_account_folder_read_date", indexes);
        Assert.Contains("idx_summary_account_folder_attachments_date", indexes);
    }

    [Fact]
    public async Task Pop3Completion_RefreshesTheCurrentlyVisibleLocalFolder()
    {
        var accountId = Guid.NewGuid();
        var store = CreateStore();
        var account = new AccountModel
        {
            Id = accountId,
            AccountName = "POP account",
            Username = "pop@example.test",
            BackendKind = BackendKind.Pop3Smtp,
            IsActive = true,
        };
        var inbox = Folder(accountId, "In", container: false);
        await store.SaveFoldersAsync(accountId, [inbox]);
        await store.SaveLocalMessageAsync(Message(accountId, "In", "first", "first body"));

        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());
        vm.LoadAccountList([account]);
        await vm.SelectFolderCommand.ExecuteAsync(inbox);
        Assert.Single(vm.Messages);

        var delivered = Message(accountId, "In", "second", "second body");
        await store.SaveLocalMessageAsync(delivered);

        await vm.RefreshAfterPop3ReceiveAsync(account,
            new Pop3ReceiveResult(1, 0, 1, Skipped: false));

        Assert.Equal(2, vm.Messages.Count);
        Assert.Contains(vm.Messages, message => message.MessageId == "second");
        vm.Dispose();
    }

    [Fact]
    public async Task LocalStore_MergeFolderPath_MovesMessagesIntoExistingTree()
    {
        var accountId = Guid.NewGuid();
        var store = CreateStore();
        await store.SaveFoldersAsync(accountId,
        [
            Folder(accountId, "Source", container: true),
            Folder(accountId, "Source/Company", container: false),
            Folder(accountId, "Destination", container: true),
            Folder(accountId, "Destination/Source", container: true),
            Folder(accountId, "Destination/Source/Company", container: false),
        ]);
        await store.SaveLocalMessageAsync(Message(accountId, "Source/Company", "source-message", "merge me"));
        await store.SaveLocalMessageAsync(Message(accountId, "Destination/Source/Company", "existing-message", "keep me"));

        await store.MergeFolderPathAsync(accountId, "Source", "Destination/Source");

        var merged = await store.LoadLocalPageAsync(accountId, "Destination/Source/Company", 20, 0);
        Assert.Equal(2, merged.TotalMatches);
        var folders = await store.LoadFoldersAsync();
        Assert.DoesNotContain(folders[accountId], folder => folder.FullName.StartsWith("Source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalStore_NormalizesOnlyLocalInboxIntoPhysicalIn_AndRepairsCanonicalBinding()
    {
        var local = Guid.NewGuid();
        var imap = Guid.NewGuid();
        var root = Guid.NewGuid();
        var store = CreateStore();
        await store.SaveFoldersAsync(local,
        [
            new MailFolderModel { AccountId = local, FullName = "In", DisplayName = "In", Kind = SpecialFolderKind.Inbox },
            new MailFolderModel { AccountId = local, FullName = "Inbox", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox },
        ]);
        await store.SaveLocalMessageAsync(Message(local, "In", "already-in", "existing body"));
        await store.SaveLocalMessageAsync(Message(local, "Inbox", "from-inbox", "migrated searchable body"));
        await store.SaveLocalMessageAsync(Message(local, "In", "duplicate", "destination copy wins"));
        await store.SaveLocalMessageAsync(Message(local, "Inbox", "duplicate", "source duplicate"));
        await store.SaveLocalMessageAsync(Message(imap, "Inbox", "imap-message", "server inbox body"));

        new LocalFolderTreeMigrationService().BuildShadowTree(Path.Combine(_directory, "mail.db"),
        [
            new AccountModel { Id = local, FolderTreeRootId = root, BackendKind = BackendKind.Pop3Smtp },
            new AccountModel { Id = imap, BackendKind = BackendKind.ImapSmtp },
        ]);

        var moved = store.NormalizeLocalInboxFolders([local]);

        Assert.Equal(2, moved);
        Assert.Equal(3, (await store.LoadLocalPageAsync(local, "In", 20, 0)).TotalMatches);
        Assert.Equal(0, (await store.LoadLocalPageAsync(local, "Inbox", 20, 0)).TotalMatches);
        Assert.Equal(1, (await store.LoadLocalPageAsync(imap, "Inbox", 20, 0)).TotalMatches);
        Assert.Single((await store.SearchLocalMessagesAsync(
            new LocalSearchQuery("migrated searchable body", local, "In"))).Messages);
        Assert.Equal(0, store.NormalizeLocalInboxFolders([local]));

        using var verify = new SqliteConnection($"Data Source={Path.Combine(_directory, "mail.db")};Mode=ReadOnly;Pooling=False");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM MessageSummary WHERE account_id=$local AND folder_name='Inbox'),
              (SELECT COUNT(*) FROM MessageDetail WHERE account_id=$local AND folder_name='Inbox'),
              (SELECT COUNT(*) FROM LocalMessageFtsKey WHERE account_id=$local AND folder_name='Inbox'),
              (SELECT COUNT(*) FROM Folder WHERE account_id=$local AND full_name='Inbox'),
              (SELECT COUNT(*) FROM LocalFolderBinding_shadow WHERE account_id=$local AND legacy_full_name='Inbox'),
              (SELECT COUNT(*) FROM LocalFolderBinding_shadow WHERE account_id=$local AND legacy_full_name='In');
            """;
        query.Parameters.AddWithValue("$local", local.ToString("D"));
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.Equal(0, reader.GetInt32(4));
        Assert.Equal(1, reader.GetInt32(5));
    }

    [Fact]
    public async Task Pop3Receiver_CommitsLocallyBeforeDeletingAndRediscardsKnownUidl()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), BackendKind = BackendKind.Pop3Smtp, Username = "user@example.test",
            Pop3Host = "mail.test", EncryptedPop3Password = "encrypted",
        };
        var events = new List<string>();
        var store = new RecordingStore(events);
        var transport = new FakePop3Transport(events,
        [
            ("new-uid", CreateMime("new")),
            ("known-uid", CreateMime("known")),
        ]);
        store.Known.Add("known-uid");
        var service = new Pop3ReceiveService(new FakeTransportFactory(transport), store, new FakeProtector());

        var result = await service.CheckNowAsync(account);

        Assert.Equal(1, result.Downloaded);
        Assert.Equal(1, result.RemovedAlreadyKnown);
        Assert.True(events.IndexOf("save:new-uid") < events.IndexOf("delete:0"));
        Assert.Equal("disconnect:commit", events[^1]);
        Assert.Equal("In", store.SavedMessages.Single().FolderName);
    }

    [Fact]
    public void Pop3Mapping_PreservesHeadersAndNormalizesMissingEnvelopeFields()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("support@example.test"));
        message.Headers.Add("Delivered-To", "delivered@example.test");
        message.Subject = string.Empty;
        message.Date = new DateTimeOffset(1, 1, 1, 1, 0, 0, TimeSpan.FromHours(1));
        message.Body = new TextPart("plain") { Text = "body" };
        var before = DateTimeOffset.UtcNow;

        var mapped = Pop3ReceiveService.MapMessage(
            Guid.NewGuid(), "missing-envelope-fields", message, "account@example.test");

        Assert.Equal("delivered@example.test", mapped.To);
        Assert.Equal("(no subject)", mapped.Subject);
        Assert.InRange(mapped.Date, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Contains("From: support@example.test", mapped.RawHeaders);
        Assert.Contains("Delivered-To: delivered@example.test", mapped.RawHeaders);
        Assert.NotEqual("MimeKit.HeaderList", mapped.RawHeaders);
    }

    [Fact]
    public void Pop3Mapping_ParsesAndRetainsTextCalendarPart()
    {
        const string ics = "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\nUID:pop3-invite\r\n" +
                           "SUMMARY:POP3 meeting\r\nDTSTART:20260901T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR";
        var message = new MimeMessage
        {
            Subject = "Invitation",
            Date = DateTimeOffset.UtcNow,
            Body = new Multipart("mixed")
            {
                new TextPart("plain") { Text = "Please join us." },
                new TextPart("calendar")
                {
                    Text = ics,
                    FileName = "meeting.ics",
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                },
            },
        };
        message.From.Add(MailboxAddress.Parse("organizer@example.test"));
        message.To.Add(MailboxAddress.Parse("user@example.test"));

        // Round-trip through the MIME parser so body-part content is backed by the same kind of
        // stream returned by a real POP3 transport (the regression did not reproduce on a freshly
        // constructed TextPart).
        using var wire = new MemoryStream();
        message.WriteTo(wire);
        wire.Position = 0;
        var parsedMessage = MimeMessage.Load(wire);

        var mapped = Pop3ReceiveService.MapMessage(Guid.NewGuid(), "ics-uidl", parsedMessage);

        Assert.Equal(ics, mapped.CalendarIcs);
        Assert.Equal("pop3-invite", mapped.CalendarInvite?.Uid);
        var attachment = Assert.Single(mapped.Attachments);
        Assert.Equal("meeting.ics", attachment.FileName);
        Assert.Equal("text/calendar", attachment.ContentType);
        Assert.NotEmpty(attachment.Content!);
    }

    [Fact]
    public void Pop3Mapping_RecognizesGenericAttachmentByIcsExtension()
    {
        const string ics = "BEGIN:VCALENDAR\r\nMETHOD:PUBLISH\r\nBEGIN:VEVENT\r\nUID:published-item\r\n" +
                           "SUMMARY:Published event\r\nDTSTART:20260902T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR";
        var calendarPart = new MimePart("application", "octet-stream")
        {
            FileName = "event.ics",
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            Content = new MimeContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ics))),
        };
        var message = new MimeMessage
        {
            Subject = "Calendar file",
            Date = DateTimeOffset.UtcNow,
            Body = new Multipart("mixed") { new TextPart("plain") { Text = "Attached." }, calendarPart },
        };
        message.From.Add(MailboxAddress.Parse("organizer@example.test"));
        message.To.Add(MailboxAddress.Parse("user@example.test"));

        var mapped = Pop3ReceiveService.MapMessage(Guid.NewGuid(), "generic-ics", message);

        Assert.Equal("published-item", mapped.CalendarInvite?.Uid);
        Assert.Equal("PUBLISH", mapped.CalendarInvite?.Method);
        Assert.Equal("event.ics", Assert.Single(mapped.Attachments).FileName);
    }

    [Fact]
    public async Task AutomaticPop3Check_RespectsPerAccountFlag()
    {
        var account = new AccountModel { BackendKind = BackendKind.Pop3Smtp, CheckIncomingMail = false };
        var factory = new ThrowingTransportFactory();
        var service = new Pop3ReceiveService(factory, new RecordingStore([]), new FakeProtector());
        var result = await service.CheckAutomaticallyAsync(account);
        Assert.True(result.Skipped);
        Assert.False(factory.WasCalled);
    }

    [Fact]
    public async Task RepliedMarker_IsPersistedAndSurvivesLaterSummaryRefresh()
    {
        var accountId = Guid.NewGuid();
        var store = CreateStore();
        var message = Message(accountId, "In", "reply-source", "Original message");
        await store.SaveLocalMessageAsync(message);

        await store.UpdateIsRepliedAsync(accountId, "In", message.MessageId, message.InternetMessageId);
        await store.UpsertSummariesAsync([new MailMessageSummary
        {
            AccountId = accountId,
            FolderName = "In",
            MessageId = message.MessageId,
            InternetMessageId = message.InternetMessageId,
            From = message.From,
            To = message.To,
            Subject = message.Subject,
            Date = message.Date,
            IsReplied = false,
        }]);

        var reloaded = Assert.Single(await store.LoadFolderSummariesAsync(accountId, "In"));
        Assert.True(reloaded.IsReplied);
        Assert.Equal("↩", reloaded.ReplyIndicator);
    }

    private LocalStoreService CreateStore()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory));
        store.Initialize();
        return store;
    }

    private static MailFolderModel Folder(Guid accountId, string name, bool container) => new()
    { AccountId = accountId, FullName = name, DisplayName = name, IsContainer = container };

    private static MailMessageDetail Message(Guid accountId, string folder, string id, string body) => new()
    {
        AccountId = accountId, FolderName = folder, MessageId = id, InternetMessageId = $"<{id}@test>",
        From = "sender@example.test", To = "receiver@example.test", Cc = "copy@example.test",
        Subject = "Subject", Date = DateTimeOffset.UtcNow, PlainTextBody = body, Preview = body,
    };

    private static MimeMessage CreateMime(string subject)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("sender@example.test"));
        message.To.Add(MailboxAddress.Parse("receiver@example.test"));
        message.Subject = subject;
        message.Date = DateTimeOffset.UtcNow;
        message.Body = new TextPart("plain") { Text = "body" };
        return message;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeProtector : IAccountSecretProtector
    {
        public string Protect(string secret) => "encrypted";
        public string? Unprotect(string protectedSecret) => "password";
        public void SetPop3Password(AccountModel account, string password) { }
        public void SetSmtpPassword(AccountModel account, string password) { }
        public string? GetPop3Password(AccountModel account) => "password";
        public string? GetSmtpPassword(AccountModel account) => "password";
    }

    private sealed class FakeTransportFactory(IPop3Transport transport) : IPop3TransportFactory
    { public IPop3Transport Create(AccountModel account) => transport; }

    private sealed class ThrowingTransportFactory : IPop3TransportFactory
    {
        public bool WasCalled { get; private set; }
        public IPop3Transport Create(AccountModel account) { WasCalled = true; throw new InvalidOperationException(); }
    }

    private sealed class FakePop3Transport(List<string> events, IReadOnlyList<(string Uidl, MimeMessage Message)> mail)
        : IPop3Transport
    {
        public bool SupportsUidListing => true;
        public Task ConnectAsync(AccountModel account, CancellationToken ct) { events.Add("connect"); return Task.CompletedTask; }
        public Task AuthenticateAsync(string username, string password, CancellationToken ct) { events.Add("auth"); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> GetMessageUidsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(mail.Select(m => m.Uidl).ToList());
        public Task<MimeMessage> GetMessageAsync(int index, CancellationToken ct) => Task.FromResult(mail[index].Message);
        public Task DeleteMessageAsync(int index, CancellationToken ct) { events.Add($"delete:{index}"); return Task.CompletedTask; }
        public Task DisconnectAsync(bool commitDeletes, CancellationToken ct)
        { events.Add(commitDeletes ? "disconnect:commit" : "disconnect:rollback"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingStore(List<string> events) : ILocalMailboxStore
    {
        public HashSet<string> Known { get; } = [];
        public List<MailMessageDetail> SavedMessages { get; } = [];
        public Task<bool> ContainsPop3UidAsync(Guid accountId, string uidl, CancellationToken ct = default) =>
            Task.FromResult(Known.Contains(uidl));
        public Task SavePop3MessageAsync(string uidl, MailMessageDetail message, CancellationToken ct = default)
        { events.Add($"save:{uidl}"); Known.Add(uidl); SavedMessages.Add(message); return Task.CompletedTask; }
        public Task SaveLocalMessageAsync(MailMessageDetail message, CancellationToken ct = default) => Task.CompletedTask;
        public Task<LocalSearchResult> SearchLocalMessagesAsync(LocalSearchQuery query, CancellationToken ct = default) =>
            Task.FromResult(new LocalSearchResult([], 0));
        public Task<LocalSearchResult> LoadLocalPageAsync(Guid? accountId, string? folderName, int limit, int offset,
            LocalSearchSort sort = LocalSearchSort.NewestFirst, CancellationToken ct = default) =>
            Task.FromResult(new LocalSearchResult([], 0));
        public Task MoveLocalMessagesAsync(Guid accountId, string sourceFolder, string destinationFolder,
            IReadOnlyCollection<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteLocalMessagesAsync(Guid accountId, string folderName,
            IReadOnlyCollection<string> messageIds, CancellationToken ct = default) => Task.CompletedTask;
    }
}
