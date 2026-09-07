using QuickMail.Models;
using System.IO;

namespace QuickMail.Services;

public interface IImapBodyBackfillService
{
    event Action<ImapBodyBackfillProgress>? ProgressChanged;
    Task RunAsync(IReadOnlyCollection<AccountModel> accounts, CancellationToken ct = default);
}

/// <summary>
/// Completes the local full-text index for IMAP mail. Work is selected from SQLite, so cancellation
/// or an application restart merely resumes the remaining rows. RFC Message-ID collapses Gmail label
/// copies: one server fetch is propagated to every local physical copy of the same logical message.
/// </summary>
public sealed class ImapBodyBackfillService : IImapBodyBackfillService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(15);
    private readonly IMailService _mail;
    private readonly ILocalStoreService _store;
    private int _running;

    public ImapBodyBackfillService(IMailService mail, ILocalStoreService store)
    {
        _mail = mail;
        _store = store;
    }

    public event Action<ImapBodyBackfillProgress>? ProgressChanged;

    public async Task RunAsync(IReadOnlyCollection<AccountModel> accounts, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            var ids = accounts.Where(a => a.IsActive && a.BackendKind == BackendKind.ImapSmtp)
                              .Select(a => a.Id).Distinct().ToArray();
            if (ids.Length == 0) return;

            using var timing = PerformanceLogService.Measure("IMAP body index: background backfill",
                $"accounts={ids.Length}");
            var retryBefore = DateTimeOffset.UtcNow - ErrorRetryDelay;
            var total = await _store.CountPendingImapBodiesAsync(ids, retryBefore, ct);
            long completed = 0;
            var errors = 0;
            if (total > 0) ProgressChanged?.Invoke(new(0, total, 0));

            while (!ct.IsCancellationRequested)
            {
                var batch = await _store.LoadPendingImapBodiesAsync(ids, BatchSize, retryBefore, ct);
                if (batch.Count == 0) break;
                foreach (var candidate in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await _store.MarkImapBodyDownloadStartedAsync(candidate, ct);
                        var detail = await _store.LoadCachedImapBodyByInternetMessageIdAsync(
                            candidate.AccountId, candidate.InternetMessageId, ct)
                            ?? await _mail.PrefetchMessageDetailAsync(candidate.AccountId,
                                candidate.FolderName, candidate.MessageId, ct);
                        if (detail.BodyFetchFailed)
                            throw new IOException("The IMAP server did not return the message body.");

                        var status = string.IsNullOrWhiteSpace(detail.PlainTextBody)
                                     && string.IsNullOrWhiteSpace(detail.HtmlBody)
                            ? ImapBodyCacheStatus.NoTextBody
                            : ImapBodyCacheStatus.Downloaded;
                        await _store.SaveImapBodyAndCopiesAsync(candidate, detail, status, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        errors++;
                        await _store.MarkImapBodyDownloadErrorAsync(candidate, ex.Message, ct);
                        LogService.Log($"IMAP body index: {candidate.AccountId}/{candidate.FolderName}/{candidate.MessageId}: {ex.Message}");
                    }
                    completed++;
                    ProgressChanged?.Invoke(new(completed, total, errors));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogService.Log("IMAP body index", ex);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
