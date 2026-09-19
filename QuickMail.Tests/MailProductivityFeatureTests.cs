using System.IO;
using Microsoft.Data.Sqlite;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class MailProductivityFeatureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "quickmail-productivity-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Te adjunto la factura solicitada.")]
    [InlineData("Please find the report attached.")]
    [InlineData("Incloc l'arxiu adjunt.")]
    public void ForgottenAttachmentDetector_FindsSupportedPhrases(string body) =>
        Assert.True(ForgottenAttachmentDetector.MentionsAttachment(body));

    [Fact]
    public void ForgottenAttachmentDetector_IgnoresQuotedHistory()
    {
        const string body = "Thanks.\n\n-----Original Message-----\nPlease see the attached invoice.";
        Assert.False(ForgottenAttachmentDetector.MentionsAttachment(body));
    }

    [Fact]
    public void UnsubscribeDetector_PrefersListUnsubscribeHeader()
    {
        var message = Detail(Guid.NewGuid(), "Inbox", "newsletter", "body");
        message.From = "Newsletter <news@example.test>";
        message.RawHeaders = "List-Unsubscribe: <https://example.test/leave?id=42>\r\nSubject: news";

        var candidate = Assert.IsType<UnsubscribeCandidate>(UnsubscribeDetector.Detect(message));

        Assert.Equal("https://example.test/leave?id=42", candidate.Url.AbsoluteUri);
        Assert.Equal("news@example.test", candidate.SourceKey);
    }

    [Fact]
    public void MailActivityPolicy_PersistsOfflineAndFocusModes()
    {
        Directory.CreateDirectory(_directory);
        var profile = new ProfileContext(_directory);
        using (var policy = new MailActivityPolicyService(profile))
        {
            policy.SetOffline();
            Assert.False(policy.CanReceive);
            Assert.False(policy.CanSend);
        }
        using (var reloaded = new MailActivityPolicyService(profile))
        {
            Assert.Equal(MailActivityMode.Offline, reloaded.Mode);
            reloaded.StartFocus(TimeSpan.FromMinutes(30));
            Assert.False(reloaded.CanReceive);
            Assert.True(reloaded.CanSend);
            reloaded.GoOnline();
            Assert.True(reloaded.CanReceive);
        }
    }

    [Fact]
    public async Task Snooze_HidesFromFolderButNotSearch_ThenReturnsUnread()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory));
        store.Initialize();
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), AccountName = "Gmail", Username = "user@example.test",
            BackendKind = BackendKind.ImapSmtp, IsActive = true,
        };
        await store.EnsureLocalSystemFoldersAsync([account]);
        await store.SaveFoldersAsync(account.Id,
        [
            new MailFolderModel
            {
                AccountId = account.Id, FullName = "Inbox", DisplayName = "Inbox",
                Kind = SpecialFolderKind.Inbox,
            },
        ]);
        Assert.Contains((await store.LoadFoldersAsync())[account.Id],
            folder => folder.Kind == SpecialFolderKind.Snoozed);

        var message = Detail(account.Id, "Inbox", "message-1", "needle for snooze search");
        message.Date = new DateTimeOffset(2026, 8, 1, 9, 30, 0, TimeSpan.Zero);
        message.IsRead = true;
        await store.SaveLocalMessageAsync(message);
        var wakeAt = DateTimeOffset.UtcNow.AddHours(1);
        await store.SnoozeMessagesAsync(
            [new SnoozedMessageKey(account.Id, "Inbox", message.MessageId)],
            wakeAt);

        Assert.Empty((await store.LoadLocalPageAsync(account.Id, "Inbox", 100, 0)).Messages);
        Assert.Empty(await store.LoadFolderSummariesAsync(account.Id, "Inbox"));
        Assert.Single((await store.SearchLocalMessagesAsync(
            new LocalSearchQuery("needle", account.Id, "Inbox"))).Messages);
        var snoozed = Assert.Single(await store.LoadSnoozedMessagesAsync([account.Id]));
        Assert.Equal(wakeAt, snoozed.Date);

        var awakened = await store.WakeDueSnoozedMessagesAsync(DateTimeOffset.UtcNow.AddHours(2));
        Assert.Single(awakened);
        Assert.False(awakened[0].IsRead);
        var returned = Assert.Single((await store.LoadLocalPageAsync(account.Id, "Inbox", 100, 0)).Messages);
        Assert.False(returned.IsRead);
    }

    [Fact]
    public void SnoozeReminder_BuildsCompactSelfAddressedHtmlWithRoundTripLink()
    {
        var account = new AccountModel
        {
            Id = Guid.NewGuid(), Username = "me@example.test", BackendKind = BackendKind.ImapSmtp,
        };
        var original = Detail(account.Id, "Clients/Smith & Sons", "message/42", "A useful preview");
        original.From = "Alice <alice@example.test>";

        var reminder = SnoozeReminderBuilder.Build(original, account);

        Assert.Equal(account.Id, reminder.AccountId);
        Assert.Equal("me@example.test", reminder.To);
        Assert.Equal("Reminder: Subject", reminder.Subject);
        Assert.Empty(reminder.Attachments);
        Assert.Contains("Open original message", reminder.HtmlBody, StringComparison.Ordinal);
        Assert.True(SnoozeReminderBuilder.TryParseLink(
            SnoozeReminderBuilder.BuildLink(original), out var target));
        Assert.Equal(original.AccountId, target.AccountId);
        Assert.Equal(original.FolderName, target.FolderName);
        Assert.Equal(original.MessageId, target.MessageId);
        Assert.Equal(original.InternetMessageId, target.InternetMessageId);
    }

    [Fact]
    public async Task SnoozeReminder_ResolvesOriginalAfterItWasMoved()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStoreService(new ProfileContext(_directory));
        store.Initialize();
        var accountId = Guid.NewGuid();
        await store.SaveFoldersAsync(accountId,
        [
            new MailFolderModel { AccountId = accountId, FullName = "Inbox", DisplayName = "Inbox" },
            new MailFolderModel { AccountId = accountId, FullName = "Archive", DisplayName = "Archive" },
        ]);
        var original = Detail(accountId, "Inbox", "move-me", "preview");
        await store.SaveLocalMessageAsync(original);
        var target = new SnoozeReminderTarget(accountId, "Inbox", original.MessageId,
            original.InternetMessageId);

        await store.MoveLocalMessagesAsync(accountId, "Inbox", "Archive", [original.MessageId]);
        var resolved = await store.ResolveSnoozeReminderTargetAsync(target);

        Assert.NotNull(resolved);
        Assert.Equal("Archive", resolved.FolderName);
        Assert.Equal(original.MessageId, resolved.MessageId);
    }

    private static MailMessageDetail Detail(Guid accountId, string folder, string id, string body) => new()
    {
        AccountId = accountId,
        FolderName = folder,
        MessageId = id,
        InternetMessageId = $"<{id}@example.test>",
        From = "sender@example.test",
        To = "receiver@example.test",
        Subject = "Subject",
        Date = DateTimeOffset.UtcNow,
        PlainTextBody = body,
        Preview = body,
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
