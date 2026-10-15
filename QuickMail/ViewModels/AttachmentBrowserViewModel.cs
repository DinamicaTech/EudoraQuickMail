using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class AttachmentBrowserViewModel : ObservableObject, IDisposable
{
    private readonly LocalStoreService? _store;
    private readonly IMailService _mail;
    private readonly Func<Task<List<MailMessageSummary>>> _loadScope;
    private CancellationTokenSource? _work;
    private bool _disposed;
    private string? _recoveryHash;
    private AttachmentBrowserItem? _recoveryItem;
    public ObservableCollection<AttachmentBrowserItem> Items { get; } = [];
    public ObservableCollection<AttachmentRecoveryCandidate> Candidates { get; } = [];
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _everywhere;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Cached attachment metadata only. Embedded images are excluded.";
    [ObservableProperty] private AttachmentBrowserItem? _selected;
    [ObservableProperty] private AttachmentRecoveryCandidate? _candidate;
    public string ScopeLabel { get; }

    public AttachmentBrowserViewModel(LocalStoreService? store, IMailService mail,
        Func<Task<List<MailMessageSummary>>> loadScope, string scopeLabel)
    { _store = store; _mail = mail; _loadScope = loadScope; ScopeLabel = scopeLabel; }

    partial void OnSelectedChanged(AttachmentBrowserItem? value)
    { Candidates.Clear(); Candidate = null; _recoveryItem = null; _recoveryHash = null; }

    private async Task RunAsync(Func<CancellationToken,Task> action)
    {
        if (_disposed || IsBusy) return;
        using var work = new CancellationTokenSource();
        _work = work; IsBusy = true;
        try { await action(work.Token); }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { Status = "Cancelled."; }
        catch (Exception ex) { Status = ex.Message; LogService.Log("Attachment browser", ex); }
        finally { IsBusy = false; _work = null; }
    }

    public Task SearchAsync() => RunAsync(async ct =>
    {
        if (_store == null) throw new InvalidOperationException("The attachment catalogue requires the local store; it is unavailable in online-only mode.");
        Status = "Searching cached attachment metadata…";
        var search = Search; var everywhere = Everywhere;
        var scope = everywhere ? null : await _loadScope();
        ct.ThrowIfCancellationRequested();
        var rows = await Task.Run(() => _store.BrowseAttachmentsAsync(search, scope, ct), ct);
        ct.ThrowIfCancellationRequested();
        Items.Clear(); Selected = null;
        foreach (var row in rows.Take(10000)) Items.Add(row);
        Selected = Items.FirstOrDefault();
        Status = rows.Count > 10000 ? "Showing the newest 10,000 attachments. Refine the search. Sorting applies to displayed rows."
            : $"{rows.Count:N0} attachments. Only messages with cached attachment metadata are included; server-only messages are not scanned.";
    });

    public Task SaveAsync(IReadOnlyList<AttachmentBrowserItem> selected, string destination) => RunAsync(async ct =>
    {
        var failed = new List<string>(); var saved = 0;
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            Status = $"Saving {saved + failed.Count + 1:N0}/{selected.Count:N0}: {item.Name}";
            try
            {
                await Task.Run(async () =>
                {
                    if (Path.IsPathFullyQualified(item.SourcePath) && !File.Exists(item.SourcePath) && !item.Attachment.IsLoaded)
                        throw new FileNotFoundException("Local attachment missing. Use Locate missing attachment.",item.SourcePath);
                    var detail = new MailMessageDetail { AccountId = item.Message.AccountId, MessageId = item.Message.MessageId, FolderName = item.Folder };
                    var path = await AttachmentPathMaterializer.EnsureLocalPathAsync(item.Attachment, detail, _mail, ct);
                    await AttachmentRecoveryService.CopyUniqueAsync(path, destination, item.Name, ct);
                },ct);
                saved++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failed.Add($"{item.Name}: {ex.Message}"); }
        }
        Status = $"Saved {saved:N0}; failed {failed.Count:N0}." + (failed.Count == 0 ? "" : "\n" + string.Join("\n", failed));
    });

    public Task FindMissingAsync(string directory) => RunAsync(async ct =>
    {
        var item = Selected ?? throw new InvalidOperationException("Select an attachment first.");
        if (_store == null) throw new InvalidOperationException("Recovery requires the local store.");
        if (!Path.IsPathFullyQualified(item.SourcePath)) throw new InvalidOperationException("This is a server attachment, not a missing local file. Use Save selected to download it.");
        if (await Task.Run(() => File.Exists(item.SourcePath),ct)) throw new InvalidOperationException("The original attachment still exists; no repair is needed.");
        Candidates.Clear(); Candidate = null;
        Status = "Searching the selected directory (including subdirectories)…";
        var progress = new Progress<string>(text => { if (!_disposed) Status = text; });
        _recoveryHash = await _store.GetRecoveryHashAsync(item.SourcePath, ct);
        var found = await Task.Run(() => AttachmentRecoveryService.FindAsync(item, directory, _recoveryHash, progress, ct), ct);
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(item, Selected)) return;
        _recoveryItem = item;
        foreach (var candidate in found) Candidates.Add(candidate);
    });

    public Task RepairAsync(bool copy) => RunAsync(async ct =>
    {
        var item = Selected; var candidate = Candidate;
        if (item == null || candidate == null || !ReferenceEquals(item, _recoveryItem) || _store == null)
            throw new InvalidOperationException("Select a candidate from a completed search for this attachment.");
        var expectedHash = _recoveryHash;
        var path = await Task.Run(async () =>
        {
            if (File.Exists(item.SourcePath)) throw new IOException("The original file is available again. Refresh before repairing.");
            await AttachmentRecoveryService.VerifyAsync(candidate.Path, expectedHash, ct);
            var replacement = copy ? await AttachmentRecoveryService.CopyUniqueAsync(candidate.Path, _store.RecoveredAttachmentDirectory, item.Name, ct) : candidate.Path;
            await AttachmentRecoveryService.VerifyAsync(replacement, expectedHash, ct);
            try { await _store.RelinkAttachmentAsync(item, replacement, ct); }
            catch (Exception ex) when (copy && ex is not OperationCanceledException)
            { throw new IOException($"The file was copied to {replacement}, but its message reference could not be updated: {ex.Message}",ex); }
            return replacement;
        },ct);
        item.Attachment.PartSpecifier = path;
        var position = Items.IndexOf(item);
        if (position >= 0)
        {
            var updated = new AttachmentBrowserItem(item.Message,item.Attachment);
            Items[position] = updated; Selected = updated;
        }
        Candidates.Clear(); Candidate = null; _recoveryItem = null;
        Status = $"Attachment repaired: {path}. Reopen the message to refresh its attachment list. No backup file was deleted.";
    });

    public void Cancel() => _work?.Cancel();
    public void Dispose() { _disposed = true; Cancel(); GC.SuppressFinalize(this); }
}
