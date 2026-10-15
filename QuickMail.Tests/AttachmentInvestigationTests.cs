using System.IO;
using System.Security.Cryptography;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;

namespace QuickMail.Tests;

public sealed class AttachmentInvestigationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),"EQM-investigation-" + Guid.NewGuid().ToString("N"));
    private readonly LocalStoreService _store;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    public AttachmentInvestigationTests()
    { Directory.CreateDirectory(_root); _store = new(new ProfileContext(_root)); _store.Initialize(); }
    private async Task<AttachmentBrowserItem> SeedAsync(string name = "invoice.pdf", Guid? account = null)
    {
        var message = new MailMessageDetail { AccountId = account ?? Guid.NewGuid(), MessageId = "1", FolderName = "In",
            Subject = "Invoice", From = "alice@example.com", Date = DateTimeOffset.Now,
            Attachments = [new() { FileName = name, FileSize = 3, PartSpecifier = Path.Combine(_root,"missing",name) },
                new() { FileName = "image.png", IsInline = true, PartSpecifier = "2" }] };
        await _store.UpsertSummariesAsync([message]); await _store.UpsertDetailAsync(message);
        return new(message,message.Attachments[0]);
    }
    [Fact]
    public async Task CatalogueScopesIdentityAndIncludesLegacyMissingPathsWithoutInlineImages()
    {
        var first = await SeedAsync(); await SeedAsync(account: Guid.NewGuid());
        Assert.Equal(2,(await _store.BrowseAttachmentsAsync("pdf",null,_ct)).Count);
        var item = Assert.Single(await _store.BrowseAttachmentsAsync("alice",[first.Message],_ct));
        Assert.Equal(first.Message.AccountId,item.Message.AccountId); Assert.Equal(first.SourcePath,item.SourcePath);
        Assert.Empty(await _store.BrowseAttachmentsAsync("",[],_ct));
        Assert.Single(await _store.BrowseAttachmentsAsync("",[first.Message],_ct));
        Assert.Empty(await _store.BrowseAttachmentsAsync("%",null,_ct));
    }
    [Fact]
    public async Task RepairChangesOnlySelectedReferenceAndRejectsStaleSelection()
    {
        var first = await SeedAsync(); var other = await SeedAsync(account: Guid.NewGuid());
        var replacement = Path.Combine(_root,"found.pdf"); await File.WriteAllBytesAsync(replacement,[1,2,3],_ct);
        await _store.RelinkAttachmentAsync(first,replacement,_ct);
        var loaded = await _store.LoadDetailAsync(first.Message.AccountId,"In","1");
        Assert.Equal(replacement,loaded!.Attachments[0].PartSpecifier);
        var unchanged = await _store.LoadDetailAsync(other.Message.AccountId,"In","1");
        Assert.Equal(other.SourcePath,unchanged!.Attachments[0].PartSpecifier);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.RelinkAttachmentAsync(first,replacement,_ct));
        Assert.Single(await _store.LoadMessageActivityAsync(first.Message,_ct),a => a.Action == "Attachment repaired");
    }
    [Fact]
    public async Task SearchReportsHashMismatchAndVerificationRejectsIt()
    {
        var item = await SeedAsync(); var backup = Path.Combine(_root,"backup"); Directory.CreateDirectory(backup);
        var file = Path.Combine(backup,item.Name); await File.WriteAllBytesAsync(file,[1,2,3],_ct);
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] {1,2,3}));
        Assert.Equal("SHA-256 verified",Assert.Single(await AttachmentRecoveryService.FindAsync(item,backup,hash,null,_ct)).Evidence);
        var wrong = Convert.ToHexString(SHA256.HashData(new byte[] {4,5,6}));
        Assert.Contains("mismatch",Assert.Single(await AttachmentRecoveryService.FindAsync(item,backup,wrong,null,_ct)).Evidence);
        await Assert.ThrowsAsync<IOException>(() => AttachmentRecoveryService.VerifyAsync(file,wrong,_ct));
    }
    [Fact]
    public async Task CopiesNeverOverwriteAndPreserveBackupSource()
    {
        var file = Path.Combine(_root,"invoice.pdf"); await File.WriteAllBytesAsync(file,[1,2,3],_ct);
        var copied = await AttachmentRecoveryService.CopyUniqueAsync(file,_root,"invoice.pdf",_ct);
        Assert.Equal("invoice (2).pdf",Path.GetFileName(copied));
        Assert.Equal(await File.ReadAllBytesAsync(file,_ct),await File.ReadAllBytesAsync(copied,_ct));
    }
    [Fact]
    public async Task CancelledSearchDoesNotReturnCandidates()
    {
        var item = await SeedAsync(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AttachmentRecoveryService.FindAsync(item,_root,null,null,cancelled.Token));
    }
    [Fact]
    public async Task OnlineOnlyExplainsUnavailableCatalogue()
    {
        using var vm = new AttachmentBrowserViewModel(null,new StubImapMailService(),() => Task.FromResult(new List<MailMessageSummary>()),"In");
        await vm.SearchAsync(); Assert.Contains("online-only",vm.Status); Assert.False(vm.IsBusy);
    }
    [Fact]
    public async Task RecoveryWorkflowCopiesCandidateAndKeepsBackup()
    {
        var item = await SeedAsync(); var backup = Path.Combine(_root,"backup"); Directory.CreateDirectory(backup);
        var source = Path.Combine(backup,item.Name); await File.WriteAllBytesAsync(source,[1,2,3],_ct);
        using var vm = new AttachmentBrowserViewModel(_store,new StubImapMailService(),() => Task.FromResult(new List<MailMessageSummary> { item.Message }),"In");
        await vm.SearchAsync(); Assert.NotNull(vm.Selected);
        await vm.FindMissingAsync(backup); vm.Candidate = Assert.Single(vm.Candidates);
        await vm.RepairAsync(true);
        var repaired = await _store.LoadDetailAsync(item.Message.AccountId,"In","1");
        var path = repaired!.Attachments[0].PartSpecifier!;
        Assert.StartsWith(_store.RecoveredAttachmentDirectory,path);
        Assert.True(File.Exists(path)); Assert.True(File.Exists(source));
        Assert.Equal(path,vm.Selected!.SourcePath); Assert.Empty(vm.Candidates);
    }
    [Fact]
    public async Task CandidateChangedAfterSearchCannotReplaceKnownHash()
    {
        var item = await SeedAsync(); var backup = Path.Combine(_root,"backup"); Directory.CreateDirectory(backup);
        var source = Path.Combine(backup,item.Name); await File.WriteAllBytesAsync(source,[1,2,3],_ct);
        await _store.SaveAttachmentHashAsync(item.SourcePath,3,1,Convert.ToHexString(SHA256.HashData(new byte[] {1,2,3})),_ct);
        using var vm = new AttachmentBrowserViewModel(_store,new StubImapMailService(),() => Task.FromResult(new List<MailMessageSummary> { item.Message }),"In");
        await vm.SearchAsync(); await vm.FindMissingAsync(backup); vm.Candidate = Assert.Single(vm.Candidates);
        await File.WriteAllBytesAsync(source,[4,5,6],_ct);
        await vm.RepairAsync(false);
        Assert.Contains("does not match",vm.Status);
        Assert.Equal(item.SourcePath,(await _store.LoadDetailAsync(item.Message.AccountId,"In","1"))!.Attachments[0].PartSpecifier);
    }
    [Fact]
    public async Task RuleAnalysisReportsOrderAndCompetingActionsWithoutExecuting()
    {
        var item = await SeedAsync(); var mail = new StubImapMailService();
        var service = new RuleService(mail,_store,_root);
        service.SaveRules([
            new() { Name = "First", FromContains = "alice", TargetFolder = "A" },
            new() { Name = "Second", FromContains = "alice", TargetFolder = "B" },
            new() { Name = "Remove", FromContains = "alice", Action = RuleAction.Delete },
            new() { Name = "Forward", FromContains = "alice", Action = RuleAction.Forward, ForwardTo = "other@example.com" },
            new() { Name = "Wrong account", FromContains = "alice", AccountId = Guid.NewGuid() },
            new() { Name = "Expired", FromContains = "alice", ActiveUntil = DateTime.Today.AddDays(-1) }
        ]);
        service.ForwardMessageAsync = (_,_,_) => throw new InvalidOperationException("Must not send");
        var text = await service.AnalyzeConflictsAsync([item.Message],_ct);
        Assert.Contains("competing destination",text); Assert.Contains("deletion combined",text);
        Assert.Contains("BLOCKED by First",text); Assert.DoesNotContain("Wrong account:",text); Assert.DoesNotContain("Expired:",text);
        Assert.NotNull(await _store.LoadDetailAsync(item.Message.AccountId,"In","1"));
    }
    [Fact]
    public async Task RuleAnalysisUsesCachedFullBodyAndCcAndFindsDuplicates()
    {
        var item = await SeedAsync(); var detail = (MailMessageDetail)item.Message;
        detail.PlainTextBody = "secret phrase"; detail.Cc = "hidden@example.com"; await _store.UpsertDetailAsync(detail);
        var service = new RuleService(new StubImapMailService(),_store,_root);
        service.SaveRules(Enumerable.Range(1,2).Select(i => new MailRule { Name = "Same" + i,
            UseFromCondition = false, UseBodyCondition = true, BodyContains = "secret phrase",
            UseToCondition = true, AlsoCcBcc = true, ToContains = "hidden", Action = RuleAction.MarkAsRead }).ToList());
        var text = await service.AnalyzeConflictsAsync([new MailMessageSummary { AccountId = detail.AccountId,MessageId = "1",FolderName = "In" }],_ct);
        Assert.Contains("Potential duplicate",text); Assert.Contains("1. Same1:",text); Assert.Contains("2. Same2:",text);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root,true); }
}
