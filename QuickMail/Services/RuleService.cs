using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public class RuleService : IRuleService
{
    private readonly string _filePath;
    private readonly IMailService _imap;
    private readonly ILocalStoreService _store;
    private readonly IAccountService? _accountService;
    private List<MailRule> _cache = [];
    private bool _loaded;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public RuleService(IMailService imap, ILocalStoreService store, string? dataDirectory = null,
        IAccountService? accountService = null)
    {
        _imap = imap;
        _store = store;
        // Optional: supplied in the app (App.xaml.cs) to drive the D1 "All accounts" → per-account
        // migration. When absent (unit tests that don't exercise migration), no migration runs.
        _accountService = accountService;
        var dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickMail");
        _filePath = Path.Combine(dir, "rules.json");
    }

    // ── Load / Save ─────────────────────────────────────────────────────────

    public List<MailRule> LoadRules()
    {
        if (_loaded) return _cache;

        if (!File.Exists(_filePath))
        {
            _cache = [];
            _loaded = true;
            return _cache;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _cache = JsonSerializer.Deserialize<List<MailRule>>(json) ?? [];
        }
        catch
        {
            _cache = [];
        }
        _loaded = true;
        ConsolidateLegacyAllAccountCopies();
        return _cache;
    }

    /// <summary>
    /// Repairs the obsolete per-account expansion used before global client rules were restored.
    /// Only semantically identical copies covering every non-Graph account are consolidated, so a
    /// genuinely account-specific rule is never broadened accidentally.
    /// </summary>
    private void ConsolidateLegacyAllAccountCopies()
    {
        if (_accountService is null || _cache.Count < 2) return;

        var accounts = _accountService.LoadAccounts();
        var targetIds = accounts.Where(a => a.BackendKind != BackendKind.MicrosoftGraph)
            .Select(a => a.Id).ToHashSet();
        if (targetIds.Count < 2) return;

        var repaired = new List<MailRule>(_cache.Count);
        var consolidated = 0;
        foreach (var group in _cache.GroupBy(SemanticKey))
        {
            var copies = group.Where(r => r.AccountId is Guid id && targetIds.Contains(id)).ToList();
            var coversAllAccounts = copies.Count == targetIds.Count
                && copies.Select(r => r.AccountId!.Value).Distinct().Count() == targetIds.Count;
            if (!coversAllAccounts)
            {
                repaired.AddRange(group);
                continue;
            }

            var representative = group.FirstOrDefault(r => r.AccountId is null) ?? copies[0];
            representative.AccountId = null;
            repaired.Add(representative);
            repaired.AddRange(group.Where(r => r.AccountId is Guid id && !targetIds.Contains(id)));
            consolidated += copies.Count - 1;
        }

        if (consolidated == 0) return;
        _cache = repaired;
        SaveRules(_cache);
        LogService.Log($"Rules repair: consolidated {consolidated} obsolete per-account rule copies into global rules.");
    }

    private static string SemanticKey(MailRule r) => string.Join('\u001f',
        r.Name, r.IsEnabled, r.ApplyAutomatically,
        r.UseFromCondition, r.FromContains,
        r.UseToCondition, r.ToContains,
        r.UseSubjectCondition, r.SubjectContains,
        r.UseBodyCondition, r.BodyContains,
        r.MustHaveAttachments, r.Action, r.AlsoMarkAsRead, r.TargetFolder);

    public void SaveRules(List<MailRule> rules)
    {
        _cache = rules;
        var dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);

        Helpers.AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(rules, JsonOptions));
        _loaded = true;
    }

    // ── Rule Execution ──────────────────────────────────────────────────────

    public async Task<(int MatchedCount, List<MailMessageSummary> RemovedMessages)> ApplyRulesAsync(
        List<MailMessageSummary> incoming,
        Guid accountId,
        CancellationToken ct)
        => await ApplyRulesCoreAsync(incoming, accountId, ct, automaticOnly: false);

    public async Task<(int MatchedCount, List<MailMessageSummary> RemovedMessages)> ApplyAutomaticRulesAsync(
        List<MailMessageSummary> incoming,
        Guid accountId,
        CancellationToken ct)
        => await ApplyRulesCoreAsync(incoming, accountId, ct, automaticOnly: true);

    private async Task<(int MatchedCount, List<MailMessageSummary> RemovedMessages)> ApplyRulesCoreAsync(
        List<MailMessageSummary> incoming,
        Guid accountId,
        CancellationToken ct,
        bool automaticOnly)
    {
        var rules = LoadRules();
        var enabledRules = rules.Where(r => r.IsEnabled && (!automaticOnly || r.ApplyAutomatically)).ToList();
        LogService.Debug($"ApplyRulesAsync: {enabledRules.Count} enabled rules, {incoming.Count} incoming messages for account {accountId}");
        if (enabledRules.Count == 0) return (0, []);

        var bodies = new Dictionary<(Guid, string, string), string>();
        if (enabledRules.Any(r => r.UseBodyCondition && !string.IsNullOrEmpty(r.BodyContains)))
        {
            foreach (var message in incoming)
            {
                var detail = await _store.LoadDetailAsync(message.AccountId, message.FolderName, message.MessageId);
                bodies[(message.AccountId, message.FolderName, message.MessageId)] = detail is null
                    ? message.Preview ?? string.Empty
                    : string.IsNullOrWhiteSpace(detail.PlainTextBody) ? detail.HtmlBody ?? string.Empty : detail.PlainTextBody;
            }
        }

        var affectedKeys = new HashSet<(string MessageId, Guid AccountId, string FolderName)>();
        var removedMessages = new List<MailMessageSummary>();

        foreach (var rule in enabledRules)
        {
            ct.ThrowIfCancellationRequested();

            // Account scope check
            if (rule.AccountId.HasValue && rule.AccountId.Value != accountId)
            {
                LogService.Debug($"  Rule '{rule.Name}': skipped (account {rule.AccountId} != {accountId})");
                continue;
            }

            var matched = incoming.Where(m => MatchesRule(rule, m,
                bodies.GetValueOrDefault((m.AccountId, m.FolderName, m.MessageId)))).ToList();
            LogService.Debug($"  Rule '{rule.Name}': {matched.Count} matched (action={rule.Action}, from='{rule.FromContains}', subject='{rule.SubjectContains}')");
            if (matched.Count > 0)
            {
                foreach (var m in matched.Take(3))
                    LogService.Debug($"    Match: From='{m.From}' Subject='{m.Subject}' UID={m.MessageId} Folder={m.FolderName}");
            }
            if (matched.Count == 0) continue;

            foreach (var m in matched)
                affectedKeys.Add((m.MessageId, m.AccountId, m.FolderName));

            try
            {
                await ExecuteActionAsync(rule, matched, accountId, ct);

                // Remove messages from incoming that were moved or deleted so the
                // UI doesn't show them in the original folder after FolderSynced fires.
                if (rule.Action is RuleAction.MoveToFolder or RuleAction.Delete)
                {
                    var matchedKeys = new HashSet<(string MessageId, Guid AccountId, string FolderName)>();
                    foreach (var m in matched)
                        matchedKeys.Add((m.MessageId, m.AccountId, m.FolderName));
                    incoming.RemoveAll(m => matchedKeys.Contains((m.MessageId, m.AccountId, m.FolderName)));
                    removedMessages.AddRange(matched);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"Rule '{rule.Name}' action failed", ex);
            }
        }

        return (affectedKeys.Count, removedMessages);
    }

    public List<MailMessageSummary> TestRule(MailRule rule, IEnumerable<MailMessageSummary> messages)
    {
        return messages.Where(m => MatchesRule(rule, m)).ToList();
    }

    // ── Condition Matching ──────────────────────────────────────────────────

    private static bool MatchesRule(MailRule rule, MailMessageSummary msg, string? completeBody = null)
    {
        if (rule.UseFromCondition
            && !string.IsNullOrEmpty(rule.FromContains)
            && !msg.From.Contains(rule.FromContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.UseToCondition
            && !string.IsNullOrEmpty(rule.ToContains)
            && !msg.To.Contains(rule.ToContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.UseSubjectCondition
            && !string.IsNullOrEmpty(rule.SubjectContains)
            && !msg.Subject.Contains(rule.SubjectContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.UseBodyCondition
            && !string.IsNullOrEmpty(rule.BodyContains)
            && !(completeBody ?? msg.Preview ?? string.Empty).Contains(rule.BodyContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.MustHaveAttachments && !msg.HasAttachments)
            return false;

        return true;
    }

    // ── Action Execution ────────────────────────────────────────────────────

    private async Task ExecuteActionAsync(
        MailRule rule,
        List<MailMessageSummary> matched,
        Guid accountId,
        CancellationToken ct)
    {
        if (rule.AlsoMarkAsRead && rule.Action != RuleAction.MarkAsRead)
            await MarkAsReadAsync(matched, ct);
        switch (rule.Action)
        {
            case RuleAction.MarkAsRead:
                await MarkAsReadAsync(matched, ct);
                break;

            case RuleAction.MarkAsUnread:
                await MarkAsUnreadAsync(matched, ct);
                break;

            case RuleAction.MoveToFolder:
                if (string.IsNullOrEmpty(rule.TargetFolder)) break;
                await MoveToFolderAsync(matched, rule.TargetFolder, ct);
                break;

            case RuleAction.Delete:
                await DeleteAsync(matched, ct);
                break;
        }
    }

    private async Task MarkAsReadAsync(List<MailMessageSummary> messages, CancellationToken ct)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _imap.MarkReadAsync(msg.AccountId, msg.FolderName, msg.MessageId, ct);
                msg.IsRead = true;
                await _store.UpdateIsReadAsync(msg.AccountId, msg.FolderName, msg.MessageId, true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"MarkRead failed for UID {msg.MessageId}", ex);
            }
        }
    }

    private async Task MarkAsUnreadAsync(List<MailMessageSummary> messages, CancellationToken ct)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // IMailService has no MarkUnreadAsync yet — we update the local store
                // only. Full server-side unread will be added in a follow-up.
                msg.IsRead = false;
                await _store.UpdateIsReadAsync(msg.AccountId, msg.FolderName, msg.MessageId, false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"MarkUnread failed for UID {msg.MessageId}", ex);
            }
        }
    }

    private async Task MoveToFolderAsync(
        List<MailMessageSummary> messages, string targetFolder, CancellationToken ct)
    {
        // Group messages by (AccountId, FolderName) so we issue one MOVE per source folder.
        var groups = messages.GroupBy(m => (m.AccountId, m.FolderName));
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var uids = group.Select(m => m.MessageId).ToList();
            try
            {
                await _imap.MoveMessagesAsync(
                    group.Key.AccountId, group.Key.FolderName, uids, targetFolder, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"MoveToFolder failed for {uids.Count} messages to '{targetFolder}'", ex);
            }
        }
    }

    private async Task DeleteAsync(List<MailMessageSummary> messages, CancellationToken ct)
    {
        var groups = messages.GroupBy(m => (m.AccountId, m.FolderName));
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var uids = group.Select(m => m.MessageId).ToList();
            try
            {
                await _imap.MoveToTrashBatchAsync(
                    group.Key.AccountId, group.Key.FolderName, uids, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"Delete (move to trash) failed for {uids.Count} messages", ex);
            }
        }
    }

    // ── Apply to existing messages ──────────────────────────────────────────

    public async Task<List<MailMessageSummary>> ApplyRulesToExistingAsync(
        ILocalStoreService store,
        IReadOnlyDictionary<Guid, string> inboxFolderByAccount,
        CancellationToken ct)
    {
        var rules = LoadRules();
        var enabledRules = rules.Where(r => r.IsEnabled).ToList();
        if (enabledRules.Count == 0) return [];

        var removedMessages = new List<MailMessageSummary>();

        // Load all cached messages once, then keep only Inbox mail (issue #346 follow-up).
        // Client rules act on the Inbox only, so drop everything in Sent/Archive/Junk/Trash/custom
        // folders — and any account we weren't given an Inbox for (fail-closed). FolderName holds the
        // folder's FullName (set at sync time), matched Ordinal against the caller-supplied Inbox
        // FullName from the same folder enumeration.
        var allMessages = await store.LoadAllSummariesAsync();
        var inboxMessages = allMessages.Where(m =>
            inboxFolderByAccount.TryGetValue(m.AccountId, out var inbox) &&
            string.Equals(m.FolderName, inbox, StringComparison.Ordinal)).ToList();
        LogService.Debug($"ApplyRulesToExisting: {allMessages.Count} cached, {inboxMessages.Count} in Inbox, {enabledRules.Count} enabled rules");

        foreach (var rule in enabledRules)
        {
            ct.ThrowIfCancellationRequested();

            var matched = inboxMessages.Where(m =>
            {
                if (rule.AccountId.HasValue && rule.AccountId.Value != m.AccountId)
                    return false;
                return MatchesRule(rule, m);
            }).ToList();

            LogService.Debug($"  Rule '{rule.Name}': {matched.Count} matched in existing mail (action={rule.Action})");
            if (matched.Count == 0) continue;

            try
            {
                await ExecuteActionAsync(rule, matched, matched[0].AccountId, ct);

                if (rule.Action is RuleAction.MoveToFolder or RuleAction.Delete)
                {
                    var byFolder = matched.GroupBy(m => (m.AccountId, m.FolderName));
                    foreach (var group in byFolder)
                    {
                        await store.DeleteSummariesAsync(
                            group.Key.AccountId, group.Key.FolderName,
                            group.Select(m => m.MessageId));
                    }
                    removedMessages.AddRange(matched);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"ApplyRulesToExisting: rule '{rule.Name}' failed", ex);
            }
        }

        return removedMessages;
    }
}
