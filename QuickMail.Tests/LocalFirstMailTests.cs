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
    public async Task CanonicalIn_KeepsBoundRootlessImapInbox_WhenAnotherRootIsAdded()
    {
        var localId = Guid.NewGuid();
        var otherRootId = Guid.NewGuid();
        var imapId = Guid.NewGuid();
        var local = new AccountModel
        {
            Id = localId, AccountName = "Local", Username = "local@example.test",
            BackendKind = BackendKind.Pop3Smtp, IsActive = true,
            FolderTreeRootName = "Eudora",
        };
        var otherRoot = new AccountModel
        {
            Id = otherRootId, AccountName = "Other", Username = "other@example.test",
            BackendKind = BackendKind.Pop3Smtp, IsActive = true,
            FolderTreeRootName = "Other root",
        };
        var imap = new AccountModel
        {
            Id = imapId, AccountName = "Gmail", Username = "gmail@example.test",
            BackendKind = BackendKind.ImapSmtp, IsActive = true,
            // Old profiles can have no explicit assignment even though their durable canonical
            // bindings prove that they belong to Eudora.
        };
        var store = CreateStore();
        await store.SaveFoldersAsync(localId,
        [
            new MailFolderModel { AccountId = localId, FullName = "In", DisplayName = "In", Kind = SpecialFolderKind.Inbox },
        ]);
        await store.SaveFoldersAsync(otherRootId,
        [
            new MailFolderModel { AccountId = otherRootId, FullName = "In", DisplayName = "In", Kind = SpecialFolderKind.Inbox },
        ]);
        await store.SaveFoldersAsync(imapId,
        [
            new MailFolderModel { AccountId = imapId, FullName = "INBOX", DisplayName = "Inbox", Kind = SpecialFolderKind.Inbox },
        ]);
        new LocalFolderTreeMigrationService().BuildShadowTree(
            Path.Combine(_directory, "mail.db"), [local, otherRoot]);
        var canonical = (await store.LoadCanonicalLocalFolderTreeAsync())!.Folders.Single(folder =>
            folder.RootId == localId && folder.CanonicalPath == "In");
        await store.EnsureCanonicalFolderBindingAsync(canonical.FolderId, imapId);

        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());
        vm.LoadAccountList([local, otherRoot, imap]);
        await vm.InitialLoadAsync();

        var sources = vm.FolderScopedAggregateSources(
            "\u0000LocalFolder:" + canonical.FolderId.ToString("D")).ToList();

        Assert.Contains(sources, source => source.Account.Id == localId && source.Folder.FullName == "In");
        Assert.Contains(sources, source => source.Account.Id == imapId && source.Folder.FullName == "INBOX");
        Assert.DoesNotContain(sources, source => source.Account.Id == otherRootId);
        vm.Dispose();
    }

    [Fact]
    public async Task CanonicalFolder_UsesBindingCreatedAfterStartupBeforePhysicalCacheRefresh()
    {
        var ownerId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var owner = new AccountModel
        {
            Id = ownerId, AccountName = "Owner", Username = "owner@example.test",
            BackendKind = BackendKind.Pop3Smtp, IsActive = true,
            FolderTreeRootId = ownerId, FolderTreeRootName = "Eudora",
        };
        var second = new AccountModel
        {
            Id = secondId, AccountName = "Second", Username = "second@example.test",
            BackendKind = BackendKind.Pop3Smtp, IsActive = true,
            FolderTreeRootId = ownerId, FolderTreeRootName = "Eudora",
        };
        var store = CreateStore();
        await store.SaveFoldersAsync(ownerId,
        [
            Folder(ownerId, "Dinamica/Prov/Internet/OVH", container: false),
        ]);
        await store.SaveFoldersAsync(secondId,
        [
            Folder(secondId, "In", container: false),
        ]);
        new LocalFolderTreeMigrationService().BuildShadowTree(
            Path.Combine(_directory, "mail.db"), [owner, second]);

        var vm = new MainViewModel(
            new StubImapMailService(), new StubAccountService(), new StubCredentialService(),
            store, new StubOAuthService(), new StubSyncService(), new StubConfigService(),
            new StubCommandRegistry(), new StubViewService(), new StubRuleService(), new StubSmtpService());
        vm.LoadAccountList([owner, second]);
        await vm.InitialLoadAsync();

        var target = (await store.LoadCanonicalLocalFolderTreeAsync())!.Folders.Single(folder =>
            folder.CanonicalPath == "Dinamica/Prov/Internet/OVH");
        await store.EnsureCanonicalFolderBindingAsync(target.FolderId, secondId);
        await store.SaveLocalMessageAsync(Message(secondId, target.CanonicalPath, "ovh", "body"));

        // Refreshes the canonical map, deliberately not the physical-folder cache: this is the
        // exact in-session state immediately after a rule moves mail for a second account.
        await vm.RefreshCanonicalFolderCountsAsync();
        var sources = vm.FolderScopedAggregateSources(
            "\u0000LocalFolder:" + target.FolderId.ToString("D")).ToList();

        Assert.Contains(sources, source => source.Account.Id == secondId &&
            source.Folder.FullName == "Dinamica/Prov/Internet/OVH");
        var visible = new List<MailMessageSummary>();
        foreach (var source in sources)
            visible.AddRange(await store.LoadFolderSummariesAsync(source.Account.Id, source.Folder.FullName));
        Assert.Contains(visible, message => message.AccountId == secondId && message.MessageId == "ovh");
        vm.Dispose();
    }

    [Fact]
    public async Task CanonicalFolderBinding_RecreatesPhysicalRowRemovedByServerFolderRefresh()
    {
        var ownerId = Guid.NewGuid();
        var imapId = Guid.NewGuid();
        var owner = new AccountModel
        {
            Id = ownerId, AccountName = "Owner", Username = "owner@example.test",
            BackendKind = BackendKind.Pop3Smtp, IsActive = true,
            FolderTreeRootId = ownerId, FolderTreeRootName = "Eudora",
        };
        var store = CreateStore();
        await store.SaveFoldersAsync(ownerId,
        [
            Folder(ownerId, "Ocio/Batalladores", container: false),
        ]);
        await store.SaveFoldersAsync(imapId,
        [
            Folder(imapId, "INBOX", container: false),
        ]);
        new LocalFolderTreeMigrationService().BuildShadowTree(
            Path.Combine(_directory, "mail.db"), [owner]);

        var target = (await store.LoadCanonicalLocalFolderTreeAsync())!.Folders.Single(folder =>
            folder.CanonicalPath == "Ocio/Batalladores");
        var binding = await store.EnsureCanonicalFolderBindingAsync(target.FolderId, imapId);

        // A server folder refresh does not yet return the destination because it has not been
        // created remotely. It replaces the Folder cache but intentionally keeps the canonical
        // binding, reproducing the Gmail filter failure seen in production.
        await store.SaveFoldersAsync(imapId,
        [
            Folder(imapId, "INBOX", container: false),
        ]);
        await store.SaveLocalMessageAsync(Message(imapId, "INBOX", "gmail-message", "body"));

        Assert.Equal(binding, await store.EnsureCanonicalFolderBindingAsync(target.FolderId, imapId));
        await store.MoveLocalMessagesAsync(imapId, "INBOX", binding, ["gmail-message"]);

        Assert.Empty(await store.LoadFolderSummariesAsync(imapId, "INBOX"));
        Assert.Single(await store.LoadFolderSummariesAsync(imapId, binding));
        Assert.Contains((await store.LoadFoldersAsync())[imapId], folder => folder.FullName == binding);
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
        Assert.Equal(8, physical.Count);
        Assert.Equal(
            new HashSet<SpecialFolderKind>
            {
                SpecialFolderKind.Inbox, SpecialFolderKind.Drafts, SpecialFolderKind.Scheduled,
                SpecialFolderKind.Snoozed, SpecialFolderKind.Sent, SpecialFolderKind.Trash,
                SpecialFolderKind.Junk,
                SpecialFolderKind.RecoveryDeleted,
            },
            physical.Select(folder => folder.Kind).ToHashSet());
        Assert.All(physical.Where(folder => folder.Kind is SpecialFolderKind.Drafts or SpecialFolderKind.Scheduled
                                               or SpecialFolderKind.RecoveryDeleted),
            folder => Assert.True(folder.ExcludeFromAllMail));
        Assert.All(physical.Where(folder => folder.Kind is SpecialFolderKind.Trash or SpecialFolderKind.Junk),
            folder => Assert.False(folder.ExcludeFromAllMail));

        var canonical = await store.LoadCanonicalLocalFolderTreeAsync();
        Assert.NotNull(canonical);
        Assert.Equal(8, canonical!.Folders.Count(folder => folder.ParentFolderId != null));
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
    public async Task RecoveryDeletedRetention_UsesFirstObservationInsteadOfMessageDate()
    {
        var accountId = Guid.NewGuid();
        var store = CreateStore();
        await store.SaveFoldersAsync(accountId,
        [
            new MailFolderModel
            {
                AccountId = accountId, FullName = "RecoveryDeleted", DisplayName = "RecoveryDeleted",
                Kind = SpecialFolderKind.RecoveryDeleted, ExcludeFromAllMail = true,
            },
        ]);
        var oldMessage = Message(accountId, "RecoveryDeleted", "recover-me", "old mail");
        oldMessage.Date = DateTimeOffset.UtcNow.AddYears(-5);
        await store.SaveLocalMessageAsync(oldMessage);

        var firstSeen = DateTimeOffset.UtcNow;
        var notExpired = await store.TrackAndGetExpiredRecoveryDeletedAsync(
            accountId, "RecoveryDeleted", ["recover-me"], firstSeen, firstSeen.AddDays(-1));
        var expiredLater = await store.TrackAndGetExpiredRecoveryDeletedAsync(
            accountId, "RecoveryDeleted", ["recover-me"], firstSeen.AddDays(2), firstSeen.AddDays(1));

        Assert.Empty(notExpired);
        Assert.Equal(["recover-me"], expiredLater);
    }

    [Fact]
    public async Task SentSaveForAccountAddedAfterStartupCreatesAndBindsOutFolderOnDemand()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "New POP", Username = "sender@example.test",
            BackendKind = BackendKind.Pop3Smtp, FolderTreeRootId = Guid.NewGuid(),
            FolderTreeRootName = "Local mail", IsActive = true,
        };
        var store = CreateStore();
        var profile = new ProfileContext(_directory);
        var accounts = new AccountService(profile);
        accounts.SaveAccounts([account]);
        var localMail = new LocalMailService(store, accounts);

        await localMail.AppendToSentAsync(account.Id, new ComposeModel
        {
            AccountId = account.Id, To = "recipient@example.test", Subject = "sent after add",
            Body = "body", Mode = ComposeMode.Html, HtmlBody = "<p>body</p>",
        });

        var sentFolder = Assert.Single((await store.LoadFoldersAsync())[account.Id]
            .Where(folder => folder.Kind == SpecialFolderKind.Sent));
        var saved = Assert.Single((await store.LoadLocalPageAsync(account.Id, sentFolder.FullName, 10, 0)).Messages);
        Assert.Equal("sender@example.test", saved.From);
        Assert.Equal("sent after add", saved.Subject);
        Assert.Equal(MessageDirection.Outgoing, saved.Direction);

        var canonical = await store.LoadCanonicalLocalFolderTreeAsync();
        Assert.Contains(canonical!.Folders, folder => folder.Kind == SpecialFolderKind.Sent &&
            folder.Bindings.Any(binding => binding.AccountId == account.Id &&
                                           binding.LegacyFullName == sentFolder.FullName));
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
    public async Task ScheduledSend_FromDraft_ConsumesDraftWithoutLeavingCopyInTrash()
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
        var compose = new ComposeModel
        {
            AccountId = account.Id, To = "recipient@example.test", Subject = "draft source",
            Body = "body", Mode = ComposeMode.Html, HtmlBody = "<p>body</p>",
        };
        var draftId = await localMail.AppendDraftAsync(account.Id, compose, null);
        compose.DraftMessageId = draftId;
        compose.DraftFolderName = "Draft";
        using var scheduled = new ScheduledSendService(
            profile, new StubSmtpService(), accounts, new StubCredentialService(), store, localMail);

        await scheduled.ScheduleAsync(compose, DateTimeOffset.Now.AddMilliseconds(25));
        await Task.Delay(75);
        var failures = await scheduled.DispatchDueAsync(manual: true);

        Assert.Empty(failures);
        Assert.Equal(0, (await store.LoadLocalPageAsync(account.Id, "Draft", 10, 0)).TotalMatches);
        Assert.Equal(0, (await store.LoadLocalPageAsync(account.Id, "Trash", 10, 0)).TotalMatches);
        Assert.Equal(1, (await store.LoadLocalPageAsync(account.Id, "Sent", 10, 0)).TotalMatches);
    }

    [Fact]
    public async Task ImmediateQueue_WakesDispatcherWithoutWaitingForPeriodicTimer()
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
        var sent = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduled = new ScheduledSendService(
            profile, new StubSmtpService(), accounts, new StubCredentialService(), store, localMail);
        scheduled.SentMailChanged += id => sent.TrySetResult(id);

        await scheduled.QueueImmediateAsync(new ComposeModel
        {
            AccountId = account.Id, To = "recipient@example.test", Subject = "send now",
            Body = "body", Mode = ComposeMode.Html, HtmlBody = "<p>body</p>",
        });

        Assert.Equal(account.Id, await sent.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(await scheduled.GetSnapshotAsync());
        Assert.Equal(0, (await store.LoadLocalPageAsync(account.Id, "Scheduled", 10, 0)).TotalMatches);
        Assert.Equal(1, (await store.LoadLocalPageAsync(account.Id, "Sent", 10, 0)).TotalMatches);
    }

    [Fact]
    public async Task DelayedImmediateQueue_CanBeUndoneIntoDraftBeforeSmtpStarts()
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
        var smtp = new StubSmtpService();
        using var scheduled = new ScheduledSendService(
            profile, smtp, accounts, new StubCredentialService(), store,
            new LocalMailService(store, accounts));
        var id = await scheduled.QueueImmediateAsync(new ComposeModel
        {
            AccountId = account.Id, To = "recipient@example.test", Subject = "undo me",
            Body = "latest body", Mode = ComposeMode.Html, HtmlBody = "<p>latest body</p>",
        }, notBeforeUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        var restored = await scheduled.UndoSendAsync(id);

        Assert.NotNull(restored);
        Assert.Equal("undo me", restored!.Subject);
        Assert.Empty(smtp.Sent);
        Assert.Empty(await scheduled.GetSnapshotAsync());
        Assert.Equal(0, (await store.LoadLocalPageAsync(account.Id, "Scheduled", 10, 0)).TotalMatches);
        var drafts = await store.LoadLocalPageAsync(account.Id, "Draft", 10, 0);
        Assert.Equal(1, drafts.TotalMatches);
        Assert.Equal("undo me", Assert.Single(drafts.Messages).Subject);
    }

    [Fact]
    public async Task ImmediateQueue_WhenNetworkFails_RemainsVisibleAndUsesBackoff()
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
        var failureObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduled = new ScheduledSendService(profile,
            new StubSmtpService { SendFailure = new IOException("network unavailable") }, accounts,
            new StubCredentialService(), store, localMail);
        scheduled.Failed += (_, _) => failureObserved.TrySetResult();

        await scheduled.QueueImmediateAsync(new ComposeModel
        {
            AccountId = account.Id, To = "recipient@example.test", Subject = "offline now",
            Body = "body", Mode = ComposeMode.Html, HtmlBody = "<p>body</p>",
        });
        await failureObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var queued = Assert.Single(await scheduled.GetSnapshotAsync());
        Assert.True(queued.IsImmediate);
        Assert.Equal(1, queued.Attempts);
        Assert.True(queued.AutomaticRetry);
        Assert.True(queued.NextAttemptUtc > DateTimeOffset.UtcNow);
        Assert.Contains("network unavailable", queued.LastError);
        Assert.Equal(1, (await store.LoadLocalPageAsync(account.Id, "Scheduled", 10, 0)).TotalMatches);
        Assert.Equal(0, (await store.LoadLocalPageAsync(account.Id, "Sent", 10, 0)).TotalMatches);
    }

    [Fact]
    public async Task ImmediateQueue_AllowsAnotherMessageToBeQueuedWhileTransportIsBusy()
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
        var sender = new BlockingSendMailService();
        using var scheduled = new ScheduledSendService(profile, sender, accounts,
            new StubCredentialService(), store, new LocalMailService(store, accounts));
        var sentCount = 0;
        var bothSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduled.SentMailChanged += _ =>
        {
            if (Interlocked.Increment(ref sentCount) == 2) bothSent.TrySetResult();
        };

        await scheduled.QueueImmediateAsync(new ComposeModel
        {
            AccountId = account.Id, To = "first@example.test", Subject = "first", Body = "body",
        });
        await sender.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // This must complete while the first SMTP call is still blocked. Holding the queue-file
        // gate for the whole network operation used to make a second Send block behind the first.
        await scheduled.QueueImmediateAsync(new ComposeModel
        {
            AccountId = account.Id, To = "second@example.test", Subject = "second", Body = "body",
        }).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, (await scheduled.GetSnapshotAsync()).Count);

        sender.AllowFirstSend.TrySetResult();
        await bothSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
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
    public async Task FolderWhitespaceNormalization_CleansPhysicalPathsAndMessageIndexes()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "POP", Username = "sender@example.test",
            BackendKind = BackendKind.Pop3Smtp, FolderTreeRootId = Guid.NewGuid(),
            FolderTreeRootName = "Local mail", IsActive = true,
        };
        var store = CreateStore();
        const string dirtyPath = "Personal/Arnau /King's InterHigh ";
        const string cleanPath = "Personal/Arnau/King's InterHigh";
        await store.SaveFoldersAsync(account.Id,
        [
            Folder(account.Id, "Personal", container: true),
            Folder(account.Id, "Personal/Arnau ", container: true),
            Folder(account.Id, dirtyPath, container: false),
        ]);
        await store.SaveLocalMessageAsync(Message(account.Id, dirtyPath, "school-message", "unique school body"));
        new LocalFolderTreeMigrationService().BuildShadowTree(
            Path.Combine(_directory, "mail.db"), [account]);

        var before = (await store.LoadCanonicalLocalFolderTreeAsync())!.Folders
            .Single(folder => folder.CanonicalPath == cleanPath);
        Assert.Contains(before.Bindings, binding => binding.LegacyFullName == dirtyPath);

        var moves = await store.NormalizeCanonicalFolderWhitespaceAsync([account.Id]);

        Assert.NotEmpty(moves);
        Assert.Equal(1, (await store.LoadLocalPageAsync(account.Id, cleanPath, 10, 0)).TotalMatches);
        Assert.Equal(0, (await store.LoadLocalPageAsync(account.Id, dirtyPath, 10, 0)).TotalMatches);
        var folders = (await store.LoadFoldersAsync())[account.Id];
        Assert.Contains(folders, folder => folder.FullName == cleanPath);
        Assert.DoesNotContain(folders, folder => folder.FullName.Contains("Arnau ", StringComparison.Ordinal));
        var after = (await store.LoadCanonicalLocalFolderTreeAsync())!.Folders
            .Single(folder => folder.CanonicalPath == cleanPath);
        Assert.Contains(after.Bindings, binding => binding.LegacyFullName == cleanPath);
    }

    [Fact]
    public async Task FolderWhitespaceNormalization_MergesWithExistingCleanFolder()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "POP", Username = "sender@example.test",
            BackendKind = BackendKind.Pop3Smtp, FolderTreeRootId = Guid.NewGuid(),
            FolderTreeRootName = "Local mail", IsActive = true,
        };
        var store = CreateStore();
        await store.SaveFoldersAsync(account.Id,
        [
            Folder(account.Id, "Customers ", container: false),
            Folder(account.Id, "Customers", container: false),
        ]);
        await store.SaveLocalMessageAsync(Message(account.Id, "Customers ", "dirty", "first"));
        await store.SaveLocalMessageAsync(Message(account.Id, "Customers", "clean", "second"));
        new LocalFolderTreeMigrationService().BuildShadowTree(
            Path.Combine(_directory, "mail.db"), [account]);

        var moves = await store.NormalizeCanonicalFolderWhitespaceAsync([account.Id]);

        Assert.Contains(moves, move => move.OldPath == "Customers " &&
            move.NewPath == "Customers" && move.WasMerged);
        Assert.Equal(2, (await store.LoadLocalPageAsync(account.Id, "Customers", 10, 0)).TotalMatches);
        Assert.Equal(0, (await store.LoadLocalPageAsync(account.Id, "Customers ", 10, 0)).TotalMatches);
        var canonical = (await store.LoadCanonicalLocalFolderTreeAsync())!.Folders
            .Single(folder => folder.CanonicalPath == "Customers");
        Assert.Single(canonical.Bindings.Where(binding => binding.AccountId == account.Id));
        Assert.Equal("Customers", canonical.Bindings.Single().LegacyFullName);
    }

    [Fact]
    public async Task FolderWhitespaceNormalization_ResumesAfterPhysicalCommit()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "POP", Username = "sender@example.test",
            BackendKind = BackendKind.Pop3Smtp, FolderTreeRootId = Guid.NewGuid(),
            FolderTreeRootName = "Local mail", IsActive = true,
        };
        var store = CreateStore();
        await store.SaveFoldersAsync(account.Id, [Folder(account.Id, "Archive ", container: false)]);
        await store.SaveLocalMessageAsync(Message(account.Id, "Archive ", "resume", "body"));
        new LocalFolderTreeMigrationService().BuildShadowTree(
            Path.Combine(_directory, "mail.db"), [account]);

        // Simulate interruption after the physical transaction committed but before its shadow
        // binding was updated. The next startup must finish metadata repair without moving twice.
        await store.RenameFolderPathAsync(account.Id, "Archive ", "Archive");

        var moves = await store.NormalizeCanonicalFolderWhitespaceAsync([account.Id]);

        Assert.Contains(moves, move => move.OldPath == "Archive " && move.NewPath == "Archive");
        Assert.Equal(1, (await store.LoadLocalPageAsync(account.Id, "Archive", 10, 0)).TotalMatches);
        var node = (await store.LoadCanonicalLocalFolderTreeAsync())!.Folders
            .Single(folder => folder.CanonicalPath == "Archive");
        Assert.Equal("Archive", Assert.Single(node.Bindings).LegacyFullName);
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

    [Fact]
    public async Task Pop3Attachments_UseOriginalNameOrdinalCollisionsAndContentReuse()
    {
        var accountId = Guid.NewGuid();
        var store = CreateStore();
        MailMessageDetail WithAttachment(string id, byte[] content)
        {
            var message = Message(accountId, "In", id, "body");
            message.Date = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);
            message.Attachments =
            [
                new AttachmentModel { FileName = "Factura.pdf", Content = content },
            ];
            return message;
        }

        var first = WithAttachment("first", [1, 2, 3]);
        var second = WithAttachment("second", [4, 5, 6]);
        var duplicate = WithAttachment("duplicate", [1, 2, 3]);
        await store.SavePop3MessageAsync("uid-first", first);
        await store.SavePop3MessageAsync("uid-second", second);
        await store.SavePop3MessageAsync("uid-duplicate", duplicate);

        Assert.Equal("Factura.pdf", Path.GetFileName(first.Attachments[0].PartSpecifier));
        Assert.Equal("Factura (2).pdf", Path.GetFileName(second.Attachments[0].PartSpecifier));
        Assert.Equal(first.Attachments[0].PartSpecifier, duplicate.Attachments[0].PartSpecifier);
        Assert.True(File.Exists(first.Attachments[0].PartSpecifier));
        Assert.True(File.Exists(second.Attachments[0].PartSpecifier));
    }

    [Fact]
    public async Task V8Migration_RemovesVerifiedHashPrefixAndUpdatesMessageReference()
    {
        var accountId = Guid.NewGuid();
        var store = CreateStore();
        var content = new byte[] { 10, 20, 30, 40 };
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        var directory = Path.Combine(_directory, "Attachments", "Received", "2026");
        Directory.CreateDirectory(directory);
        var oldPath = Path.Combine(directory, $"{hash[..16]}-Factura.pdf");
        await File.WriteAllBytesAsync(oldPath, content, TestContext.Current.CancellationToken);
        var message = Message(accountId, "In", "legacy-attachment", "body");
        message.Attachments =
        [
            new AttachmentModel
            {
                FileName = "Factura.pdf", PartSpecifier = oldPath, FileSize = content.Length,
            },
        ];
        await store.SaveLocalMessageAsync(message, TestContext.Current.CancellationToken);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "mail.db")}"))
        {
            connection.Open();
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "PRAGMA user_version=7;";
            downgrade.ExecuteNonQuery();
        }

        store.Initialize();

        var migrated = await store.LoadDetailAsync(accountId, "In", "legacy-attachment");
        var newPath = Assert.Single(migrated!.Attachments).PartSpecifier;
        Assert.Equal("Factura.pdf", Path.GetFileName(newPath));
        Assert.True(File.Exists(newPath));
        Assert.False(File.Exists(oldPath));
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

    private sealed class BlockingSendMailService : ISendMailService
    {
        private int _calls;
        public TaskCompletionSource FirstSendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowFirstSend { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(ComposeModel compose, AccountModel account, string? password,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) != 1) return;
            FirstSendStarted.TrySetResult();
            await AllowFirstSend.Task.WaitAsync(ct);
        }

        public Task SendIcsReplyAsync(string icsReplyContent, AccountModel account, string? password,
            string organizerEmail, CancellationToken ct = default) => Task.CompletedTask;

        public Task VerifyAsync(AccountModel account, string? password, CancellationToken ct = default) =>
            Task.CompletedTask;
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
