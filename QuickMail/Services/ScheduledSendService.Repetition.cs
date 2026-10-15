using System.IO;
using System.Text.Json;
using QuickMail.Models;

namespace QuickMail.Services;

public sealed partial class ScheduledSendService
{
    private async Task CompleteSentItemAsync(ScheduledMail sent)
    {
        await _gate.WaitAsync();
        try
        {
            var queue = await LoadAsync();
            var index = queue.FindIndex(x => x.Id == sent.Id);
            if (index < 0) return;
            var nextAt = sent.Message.Repetition is { } plan
                ? RepeatedSending.Next(plan, sent.SendAtUtc, DateTimeOffset.UtcNow) : null;
            if (nextAt is { } when)
            {
                var next = JsonSerializer.Deserialize<ComposeModel>(JsonSerializer.Serialize(sent.Message))!;
                next.OutgoingMessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
                next.DraftMessageId = null; next.DraftFolderName = null;
                next.ScheduledId = null; next.ScheduledLocalMessageId = null; next.ScheduledAt = null;
                var id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("next:" + sent.Id)).AsSpan(0, 16));
                var localId = "scheduled-" + id.ToString("N");
                var account = _accounts.LoadAccounts().First(x => x.Id == next.AccountId);
                await SaveScheduledLocalCopyAsync(account, next, localId, when);
                queue[index] = new ScheduledMail(id, when, next, LocalMessageId: localId);
            }
            else queue.RemoveAt(index);
            // Replace the accepted occurrence with its successor in ONE atomic queue write.
            // A restart before this write retries completion, never SMTP.
            await SaveAsync(queue);
        }
        finally { _gate.Release(); }
    }

    public async Task QueueRuleForwardAsync(ComposeModel message, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(message.RuleForwardKey)) throw new ArgumentException("Missing forward identity.");
        await _gate.WaitAsync(ct);
        try
        {
            var ledgerPath = _path + ".forwarded.json";
            // Fail closed on corrupt history: do not risk resending every historical message.
            var history = File.Exists(ledgerPath)
                ? JsonSerializer.Deserialize<HashSet<string>>(await File.ReadAllTextAsync(ledgerPath, ct))
                    ?? throw new InvalidDataException("Forward history is invalid.") : [];
            if (history.Contains(message.RuleForwardKey)) return;
            var queue = await LoadAsync();
            if (!queue.Any(x => x.Message.RuleForwardKey == message.RuleForwardKey))
            {
                var id = Guid.NewGuid();
                var localId = "scheduled-" + id.ToString("N");
                var account = _accounts.LoadAccounts().First(x => x.Id == message.AccountId);
                await _store.EnsureLocalSystemFoldersAsync([account]);
                await SaveScheduledLocalCopyAsync(account, message, localId, DateTimeOffset.UtcNow);
                queue.Add(new ScheduledMail(id, DateTimeOffset.UtcNow, message, LocalMessageId: localId, IsImmediate: true));
                await SaveAsync(queue);
            }
            history.Add(message.RuleForwardKey);
            Helpers.AtomicFile.WriteAllText(ledgerPath, JsonSerializer.Serialize(history));
        }
        finally { _gate.Release(); }
        SentMailChanged?.Invoke(message.AccountId);
        RequestDispatch();
    }
}
