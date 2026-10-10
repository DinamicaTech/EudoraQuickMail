using System.IO;
using MimeKit;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.Helpers;
using QuickMail.ViewModels;

namespace QuickMail.Tests;

public sealed class EmbeddedImageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eqm-embedded-" + Guid.NewGuid().ToString("N"));
    private const string Image = "<p>Hello</p><img width='30' src='data:image/png;base64,AQIDBA=='>";
    private EmbeddedImageStore Store() => new(new ProfileContext(_root));

    [Fact]
    public void ImageIsDurableAndSurvivesReopeningWithoutDuplicateFiles()
    {
        var store = Store();
        var saved = store.Persist(Image, []);
        var resource = Assert.Single(saved.Resources);
        Assert.StartsWith(Path.Combine(_root, "Embedded") + Path.DirectorySeparatorChar, resource.PartSpecifier);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(resource.PartSpecifier!));
        Assert.Contains("cid:" + resource.ContentId, saved.Html);
        Assert.DoesNotContain("data:", saved.Html);
        Assert.Empty(store.Persist("<p>Removed image</p>", saved.Resources).Resources);
        Assert.True(File.Exists(resource.PartSpecifier)); // another message/draft may still reference it
        var reopened = Store().Persist(Image, saved.Resources);
        Assert.Equal(resource.PartSpecifier, Assert.Single(reopened.Resources).PartSpecifier);
        Assert.Single(Directory.GetFiles(_root, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public void TemporaryAndMissingImagesCannotBeSilentlySent()
    {
        var store = Store();
        Assert.Throws<InvalidOperationException>(() => store.Persist("<img src='blob:https://quickmail.local/id'>", []));
        Assert.Throws<InvalidOperationException>(() => store.Persist("<img src='cid:missing'>", []));
    }

    [Fact]
    public void RemoteImagesAreNotFetchedAndSvgDataIsRejected()
    {
        var store = Store();
        const string remote = "<img data-src='ignored' src='https://example.com/a.png'>";
        Assert.Equal(remote, store.Persist(remote, []).Html);
        Assert.Throws<InvalidOperationException>(() => store.Persist("<img src='data:image/svg+xml;base64,AQID'>", []));
    }

    [Fact]
    public void MimeUsesRelatedResourcesNotVisibleAttachmentsAndLoadsBytesFromDisk()
    {
        var saved = Store().Persist(Image + Image, []);
        var resource = Assert.Single(saved.Resources);
        resource.Content = null;
        var compose = new ComposeModel { To = "recipient@example.com", HtmlBody = saved.Html, Attachments = saved.Resources };
        var mime = MimeMessageBuilder.Build(compose, new AccountModel { Username = "sender@example.com" });
        var alternative = Assert.IsType<MultipartAlternative>(mime.Body);
        var related = Assert.IsType<MultipartRelated>(alternative[1]);
        Assert.Equal(2, related.Count);
        var part = Assert.IsType<MimePart>(related[1]);
        Assert.Equal(resource.ContentId, part.ContentId);
        Assert.Equal("inline", part.ContentDisposition?.Disposition);
        Assert.Empty(mime.Attachments);
        using var decoded = new MemoryStream();
        part.Content!.DecodeTo(decoded);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, decoded.ToArray());
    }

    [Fact]
    public async Task LocalCacheRetainsCidAndRendersItAfterReload()
    {
        var profile = new ProfileContext(_root);
        var saved = Store().Persist(Image, []);
        var store = new LocalStoreService(profile);
        store.Initialize();
        var id = Guid.NewGuid();
        await store.SaveLocalMessageAsync(new MailMessageDetail { AccountId = id, MessageId = "test", FolderName = "Out",
            Subject = "test", HtmlBody = saved.Html, Attachments = saved.Resources, Date = DateTimeOffset.Now });
        var loaded = await store.LoadDetailAsync(id, "Out", "test");
        Assert.NotNull(loaded);
        Assert.Empty(loaded.VisibleAttachments);
        var rendered = MessageBodyHtmlBuilder.BuildMessageHtml(loaded, null, false);
        Assert.Contains("data:image/png;base64,AQIDBA==", rendered);
    }

    [Fact]
    public void ComposeSeedKeepsInlineImagesOutOfAttachmentList()
    {
        var saved = Store().Persist(Image, []);
        using var vm = new ComposeViewModel(new StubSmtpService(), new StubAccountService(), new StubCredentialService(),
            new StubImapMailService(), new StubTemplateService());
        vm.Seed(new ComposeModel { Mode = ComposeMode.Html, HtmlBody = saved.Html, Attachments = saved.Resources });
        Assert.Empty(vm.Attachments);
        Assert.Single(vm.InlineImages);
    }

    [Fact]
    public async Task DraftScheduledAndSentCopiesAllRetainTheSameDurableImage()
    {
        var profile = new ProfileContext(_root);
        var saved = Store().Persist(Image, []);
        var store = new LocalStoreService(profile);
        store.Initialize();
        var account = new AccountModel { Id = Guid.NewGuid(), Username = "sender@example.com", BackendKind = BackendKind.Pop3Smtp,
            IsActive = true, FolderTreeRootId = Guid.NewGuid(), FolderTreeRootName = "Mail" };
        var accounts = new AccountService(profile);
        accounts.SaveAccounts([account]);
        var mail = new LocalMailService(store, accounts);
        var compose = new ComposeModel { AccountId = account.Id, To = "recipient@example.com", Subject = "inline",
            Mode = ComposeMode.Html, HtmlBody = saved.Html, Attachments = saved.Resources };
        await mail.AppendDraftAsync(account.Id, compose, null);
        using (var queue = new ScheduledSendService(profile, new StubSmtpService(), accounts,
            new StubCredentialService(), store, mail))
            await queue.ScheduleAsync(compose, DateTimeOffset.Now.AddDays(1));
        await mail.AppendToSentAsync(account.Id, compose);
        var sentFolder = (await store.LoadFoldersAsync())[account.Id].Single(f => f.Kind == SpecialFolderKind.Sent).FullName;
        foreach (var folder in new[] { "Draft", "Scheduled", sentFolder })
        {
            var row = Assert.Single((await store.LoadLocalPageAsync(account.Id, folder, 10, 0)).Messages);
            var detail = await store.LoadDetailAsync(account.Id, folder, row.MessageId);
            Assert.NotNull(detail);
            Assert.Equal(saved.Resources[0].PartSpecifier, Assert.Single(detail.Attachments).PartSpecifier);
            Assert.Empty(detail.VisibleAttachments);
            Assert.Contains("data:image/png;base64,AQIDBA==", MessageBodyHtmlBuilder.BuildMessageHtml(detail, null, false));
        }
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "Embedded"), "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public void ImapRelatedImageKeepsContentIdAndInlineClassification()
    {
        var part = new MailKit.BodyPartBasic { ContentType = new ContentType("image", "png"),
            ContentId = "example@quickmail", PartSpecifier = "1.2", Octets = 4 };
        var related = new MailKit.BodyPartMultipart { ContentType = new ContentType("multipart", "related") };
        related.BodyParts.Add(part);
        var method = typeof(ImapMailService).GetMethod("ExtractAttachments", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var resource = Assert.Single(Assert.IsType<List<AttachmentModel>>(method.Invoke(null, [related])));
        Assert.True(resource.IsInline);
        Assert.Equal("example@quickmail", resource.ContentId);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
