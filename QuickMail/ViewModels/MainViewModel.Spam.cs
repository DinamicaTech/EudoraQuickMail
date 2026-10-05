using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class MainViewModel
{
    private int _spamPurgeRunning;

    private async Task PurgeDeletedAndSpamAsync()
    {
        await PurgeExpiredRecoveryDeletedAsync();
        if (IsBusy || Volatile.Read(ref _disposeState) != 0 ||
            System.Windows.Application.Current is App { MailActivityPolicy.CanReceive: false } ||
            Interlocked.Exchange(ref _spamPurgeRunning, 1) != 0) return;
        try
        {
            var days = Math.Clamp(_configService.Load().SpamRetentionDays, 0, 3650);
            if (days == 0 || _localStore is not LocalStoreService store) return;
            foreach (var account in Accounts.Where(a => a.IsActive).ToList())
            {
                if (!_cachedFolders.TryGetValue(account.Id, out var folders)) continue;
                var junk = folders.FirstOrDefault(f => f.Kind == SpecialFolderKind.Junk);
                if (junk == null) continue;
                try
                {
                    var ids = await store.SpamIdsAsync(account.Id, junk.FullName, days, _messageActionShutdownCts.Token);
                    await DeleteVerifiedSpamAsync(account.Id, junk.FullName, ids.Take(100).ToList());
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { LogService.Log("SPAM retention", ex); }
            }
        }
        finally { Interlocked.Exchange(ref _spamPurgeRunning, 0); }
    }

    public async Task EmptySpamAsync(MailFolderModel target)
    {
        if (target.Kind != SpecialFolderKind.Junk || _localStore is not LocalStoreService store) return;
        var sources = FolderScopedAggregateSources(target.FullName)
            .Where(s => s.Folder.Kind == SpecialFolderKind.Junk).ToList();
        if (sources.Count == 0 && target.AccountId != Guid.Empty)
        {
            var account = Accounts.FirstOrDefault(a => a.Id == target.AccountId);
            if (account != null) sources.Add((account, target));
        }
        var batches = new List<(Guid Account, string Folder, List<string> Ids)>();
        foreach (var source in sources)
            batches.Add((source.Account.Id, source.Folder.FullName,
                await store.SpamIdsAsync(source.Account.Id, source.Folder.FullName)));
        var count = batches.Sum(b => b.Ids.Count);
        if (count == 0) { StatusText = "No EQM-classified SPAM in Junk."; return; }
        if (ConfirmationRequested?.Invoke(
            $"Permanently delete {count:N0} EQM-classified SPAM message(s) in Junk? Other messages are kept. This cannot be undone.",
            "Delete only SPAM") != true) return;
        IsBusy = true;
        try
        {
            var deleted = 0;
            foreach (var batch in batches)
                deleted += await DeleteVerifiedSpamAsync(batch.Account, batch.Folder, batch.Ids);
            await RefreshAfterLocalMutationAsync("empty-spam");
            StatusText = $"Permanently deleted {deleted:N0} SPAM message(s).";
        }
        finally { IsBusy = false; }
    }

    private async Task<int> DeleteVerifiedSpamAsync(Guid account, string folder, List<string> ids)
    {
        if (_localStore is not LocalStoreService store || ids.Count == 0) return 0;
        var ct = _messageActionShutdownCts.Token;
        var verified = new List<string>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            // Re-read the actual envelope, not a potentially stale cached UID after a remote move.
            var remote = Accounts.FirstOrDefault(a => a.Id == account)?.BackendKind
                is BackendKind.ImapSmtp or BackendKind.MicrosoftGraph;
            var message = remote
                ? await _imap.GetMessageSummaryAsync(account, folder, id, ct)
                : await store.LoadExactSpamCandidateAsync(account, folder, id, ct);
            if (message != null && string.IsNullOrEmpty(message.InternetMessageId) &&
                remote)
                continue; // A UID alone is insufficient proof after a remote UID reset/move.
            if (message != null && await store.IsSpamAsync(message, ct))
                verified.Add(id);
        }
        if (verified.Count == 0) return 0;
        await _imap.PermanentlyDeleteBatchAsync(account, folder, verified, ct);
        await _localStore.DeleteSummariesAsync(account, folder, verified);
        await RefreshFolderListAsync(account);
        return verified.Count;
    }
}
