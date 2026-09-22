using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public class RuleService : IRuleService
{
    private const string CanonicalFolderPrefix = "\u0000LocalFolder:";
    private const string PrintableCanonicalFolderPrefix = "LocalFolder:";
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
        r.Name, r.IsEnabled, r.ApplyAutomatically, r.AlsoFilterOutMailbox,
        r.UseFromCondition, r.FromContains,
        r.UseToCondition, r.ToContains, r.AlsoCcBcc,
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

    public int RewriteFolderTargets(IReadOnlyList<CanonicalFolderMoveResult> moves)
    {
        if (moves.Count == 0) return 0;
        var rules = LoadRules();
        var changed = 0;
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.TargetFolder)) continue;
            var rewritten = Helpers.FolderReferenceRewriter.Rewrite(rule.TargetFolder, moves);
            if (string.Equals(rewritten, rule.TargetFolder, StringComparison.Ordinal)) continue;
            rule.TargetFolder = rewritten;
            changed++;
        }
        if (changed > 0) SaveRules(rules);
        return changed;
    }

    public (MailRule Rule, bool Created) SaveOrUpdateQuickMoveRule(MailRule candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Action != RuleAction.MoveToFolder || !candidate.UseFromCondition ||
            string.IsNullOrWhiteSpace(candidate.FromContains) || string.IsNullOrWhiteSpace(candidate.TargetFolder))
            throw new ArgumentException("A quick move rule requires a FROM condition and destination folder.", nameof(candidate));

        var rules = LoadRules();
        var criterion = candidate.FromContains.Trim();
        var existing = rules.FirstOrDefault(rule =>
            rule.Action == RuleAction.MoveToFolder &&
            rule.AccountId is null &&
            rule.UseFromCondition &&
            string.Equals(rule.FromContains?.Trim(), criterion, StringComparison.OrdinalIgnoreCase) &&
            !rule.UseToCondition && !rule.UseSubjectCondition && !rule.UseBodyCondition &&
            !rule.MustHaveAttachments);

        if (existing != null)
        {
            existing.TargetFolder = candidate.TargetFolder;
            existing.AlsoMarkAsRead = true;
            SaveRules(rules);
            return (existing, false);
        }

        candidate.FromContains = criterion;
        candidate.AlsoMarkAsRead = true;
        rules.Add(candidate);
        SaveRules(rules);
        return (candidate, true);
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
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var perf = $"account={accountId}; messages={incoming.Count}; automaticOnly={automaticOnly}";
        PerformanceLogService.Marker("Rules: apply batch BEGIN", perf);
        var rules = LoadRules();
        var enabledRules = rules.Where(r => r.IsEnabled && (!automaticOnly || r.ApplyAutomatically)).ToList();
        // Automatic rules are an incoming-mail pipeline. Keep this guard here as well as at the
        // sync callers so a future caller cannot accidentally apply an automatic move/delete rule
        // to an outgoing message (for example, a freshly materialized Sent copy).
        var candidates = automaticOnly
            ? incoming.Where(message => message.Direction != MessageDirection.Outgoing).ToList()
            : incoming;
        var excludedOutgoing = incoming.Count - candidates.Count;
        LogService.Debug($"ApplyRulesAsync: {enabledRules.Count} enabled rules, {candidates.Count} candidate messages for account {accountId} (automatic={automaticOnly}, outgoing excluded={excludedOutgoing})");
        if (enabledRules.Count == 0)
        {
            PerformanceLogService.Record("Rules: apply batch END",
                System.Diagnostics.Stopwatch.GetElapsedTime(started), perf + "; enabled=0");
            return (0, []);
        }

        var needsMatchData = enabledRules.Any(r =>
            (r.UseBodyCondition && !string.IsNullOrEmpty(r.BodyContains)) ||
            (r.UseToCondition && r.AlsoCcBcc && !string.IsNullOrEmpty(r.ToContains)));
        var matchData = needsMatchData
            ? await LoadRuleMatchDataAsync(_store, incoming, ct)
            : [];
        var bodiesDone = System.Diagnostics.Stopwatch.GetTimestamp();
        PerformanceLogService.Record("Rules: apply batch/load bodies",
            System.Diagnostics.Stopwatch.GetElapsedTime(started, bodiesDone),
            perf + $"; enabled={enabledRules.Count}; details={matchData.Count}");

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

            var matched = candidates.Where(m =>
            {
                matchData.TryGetValue((m.AccountId, m.FolderName, m.MessageId), out var data);
                return MatchesRule(rule, m, data?.Body, data?.Cc, data?.Bcc);
            }).ToList();
            LogService.Debug($"  Rule '{rule.Name}': {matched.Count} matched (action={rule.Action}, from='{rule.FromContains}', subject='{rule.SubjectContains}')");
            if (matched.Count > 0)
            {
                foreach (var m in matched.Take(3))
                    LogService.Debug($"    Match: From='{m.From}' Subject='{m.Subject}' UID={m.MessageId} Folder={m.FolderName}");
            }
            if (matched.Count == 0) continue;

            try
            {
                await ExecuteActionAsync(rule, matched, accountId, ct);
                foreach (var m in matched)
                    affectedKeys.Add((m.MessageId, m.AccountId, m.FolderName));

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
                if (!automaticOnly) throw;
            }
        }

        PerformanceLogService.Record("Rules: apply batch END",
            System.Diagnostics.Stopwatch.GetElapsedTime(started),
            perf + $"; enabled={enabledRules.Count}; matched={affectedKeys.Count}; removed={removedMessages.Count}");
        return (affectedKeys.Count, removedMessages);
    }

    public List<MailMessageSummary> TestRule(MailRule rule, IEnumerable<MailMessageSummary> messages)
    {
        return messages.Where(m => MatchesRule(rule, m)).ToList();
    }

    public bool IsMatch(MailRule rule, MailMessageSummary message, string? completeBody = null) =>
        (!rule.AccountId.HasValue || rule.AccountId.Value == message.AccountId) &&
        MatchesRule(rule, message, completeBody);

    public async Task<(int MatchedCount, List<MailMessageSummary> RemovedMessages)> ApplyRuleToMessagesAsync(
        MailRule rule,
        List<MailMessageSummary> messages,
        ILocalStoreService store,
        CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var perf = $"rule={rule.Name}; action={rule.Action}; messages={messages.Count}";
        PerformanceLogService.Marker("Rules: apply one BEGIN", perf);
        var candidates = rule.AccountId is { } accountId
            ? messages.Where(m => m.AccountId == accountId).ToList()
            : messages;
        var needsMatchData = (rule.UseBodyCondition && !string.IsNullOrWhiteSpace(rule.BodyContains)) ||
                             (rule.UseToCondition && rule.AlsoCcBcc && !string.IsNullOrWhiteSpace(rule.ToContains));
        var matchData = needsMatchData
            ? await LoadRuleMatchDataAsync(store, candidates, ct)
            : [];

        var matched = candidates.Where(m =>
        {
            matchData.TryGetValue((m.AccountId, m.FolderName, m.MessageId), out var data);
            return MatchesRule(rule, m, data?.Body, data?.Cc, data?.Bcc);
        }).ToList();
        var matchDone = System.Diagnostics.Stopwatch.GetTimestamp();
        PerformanceLogService.Record("Rules: apply one/evaluate",
            System.Diagnostics.Stopwatch.GetElapsedTime(started, matchDone),
            perf + $"; candidates={candidates.Count}; details={matchData.Count}; matched={matched.Count}");
        if (matched.Count == 0)
        {
            PerformanceLogService.Record("Rules: apply one END",
                System.Diagnostics.Stopwatch.GetElapsedTime(started), perf + "; matched=0");
            return (0, []);
        }

        foreach (var group in matched.GroupBy(m => m.AccountId))
            await ExecuteActionAsync(rule, group.ToList(), group.Key, ct);

        if (rule.Action is RuleAction.MoveToFolder or RuleAction.Delete)
        {
            foreach (var group in matched.GroupBy(m => (m.AccountId, m.FolderName)))
                await store.DeleteSummariesAsync(group.Key.AccountId, group.Key.FolderName,
                    group.Select(m => m.MessageId));
        }
        var removed = rule.Action is RuleAction.MoveToFolder or RuleAction.Delete ? matched : [];
        PerformanceLogService.Record("Rules: apply one END",
            System.Diagnostics.Stopwatch.GetElapsedTime(started),
            perf + $"; matched={matched.Count}; removed={removed.Count}");
        return (matched.Count, removed);
    }

    // ── Condition Matching ──────────────────────────────────────────────────

    private static bool MatchesRule(MailRule rule, MailMessageSummary msg, string? completeBody = null,
        string? storedCc = null, string? storedBcc = null)
    {
        var fromCandidate = msg.Direction == MessageDirection.Outgoing ? msg.To : msg.From;
        if (rule.UseFromCondition
            && !string.IsNullOrEmpty(rule.FromContains)
            && !MatchesText(fromCandidate, rule.FromContains))
            return false;

        if (rule.UseToCondition && !string.IsNullOrEmpty(rule.ToContains))
        {
            var recipientCandidate = rule.AlsoCcBcc
                ? string.Join(", ", new[] { msg.To, storedCc ?? msg.Cc, storedBcc ?? msg.Bcc }
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
                : msg.To;
            if (!MatchesText(recipientCandidate, rule.ToContains)) return false;
        }

        if (rule.UseSubjectCondition
            && !string.IsNullOrEmpty(rule.SubjectContains)
            && !MatchesText(msg.Subject, rule.SubjectContains))
            return false;

        if (rule.UseBodyCondition
            && !string.IsNullOrEmpty(rule.BodyContains)
            && !MatchesText(completeBody ?? msg.Preview ?? string.Empty, rule.BodyContains))
            return false;

        if (rule.MustHaveAttachments && !msg.HasAttachments)
            return false;

        return true;
    }

    private static async Task<Dictionary<(Guid AccountId, string FolderName, string MessageId), RuleMatchData>>
        LoadRuleMatchDataAsync(ILocalStoreService store, IEnumerable<MailMessageSummary> messages,
            CancellationToken ct)
    {
        var result = new Dictionary<(Guid, string, string), RuleMatchData>();
        foreach (var group in messages.GroupBy(message => (message.AccountId, message.FolderName)))
        {
            ct.ThrowIfCancellationRequested();
            var ids = group.Select(message => message.MessageId).Distinct(StringComparer.Ordinal).ToArray();
            var folderData = await store.LoadRuleMatchDataAsync(
                group.Key.AccountId, group.Key.FolderName, ids, ct);
            foreach (var pair in folderData)
                result[(group.Key.AccountId, group.Key.FolderName, pair.Key)] = pair.Value;
        }
        return result;
    }

    private static bool MatchesText(string value, string criterion)
    {
        if (!criterion.Contains('*') && !criterion.Contains('?'))
            return value.Contains(criterion, StringComparison.OrdinalIgnoreCase);
        var pattern = Regex.Escape(criterion).Replace("\\*", ".*").Replace("\\?", ".");
        return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
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
        foreach (var group in messages.GroupBy(message => (message.AccountId, message.FolderName)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var groupMessages = group.ToList();
                await _imap.MarkReadBatchAsync(group.Key.AccountId, group.Key.FolderName,
                    groupMessages.Select(message => message.MessageId).ToList(), ct);
                foreach (var message in groupMessages) message.IsRead = true;
                await _store.UpdateIsReadBatchAsync(groupMessages.Select(message =>
                    (message.AccountId, message.FolderName, message.MessageId)), true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"MarkRead failed for {group.Count()} messages in {group.Key.FolderName}", ex);
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
            var physicalTarget = await ResolveTargetFolderAsync(targetFolder, group.Key.AccountId);
            try
            {
                await _imap.MoveMessagesAsync(
                    group.Key.AccountId, group.Key.FolderName, uids, physicalTarget, ct);

                // IMAP and Graph are server-authoritative, but the message list and canonical local
                // tree are rendered from the SQLite cache. Reflect a successful remote move there
                // immediately; otherwise the source keeps a ghost row and the new destination looks
                // empty until a later reconciliation. Local/POP backends already perform this move
                // inside LocalMailService, so they must not be moved twice.
                var backend = _accountService?.LoadAccounts()
                    .FirstOrDefault(account => account.Id == group.Key.AccountId)?.BackendKind;
                if (backend is BackendKind.ImapSmtp or BackendKind.MicrosoftGraph &&
                    _store is ILocalMailboxStore mailboxStore)
                {
                    // The folder catalogue may have refreshed while the remote MOVE was running.
                    // Resolve again so an existing canonical binding repairs its physical cache row
                    // before MoveLocalMessagesAsync validates the destination.
                    physicalTarget = await ResolveTargetFolderAsync(targetFolder, group.Key.AccountId);
                    await mailboxStore.MoveLocalMessagesAsync(
                        group.Key.AccountId, group.Key.FolderName, physicalTarget, uids, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Log($"MoveToFolder failed for {uids.Count} messages to '{physicalTarget}'", ex);
                throw new InvalidOperationException(
                    $"Could not move {uids.Count:N0} message(s) to '{physicalTarget}': {ex.Message}", ex);
            }
        }
    }

    private async Task<string> ResolveTargetFolderAsync(string targetFolder, Guid accountId)
    {
        // Rules saved before the local tree became canonical may still name the old POP3 alias.
        // Resolve it per account instead of rewriting a global rule that could also cover a real
        // IMAP Inbox. IMAP/Graph accounts retain their server-owned target unchanged.
        if (targetFolder.Equals("Inbox", StringComparison.Ordinal) &&
            _accountService?.LoadAccounts().FirstOrDefault(account => account.Id == accountId)?.BackendKind
                is BackendKind.Pop3Smtp or BackendKind.LocalArchive)
            targetFolder = "In";

        var tree = await _store.LoadCanonicalLocalFolderTreeAsync();
        if (tree == null) return targetFolder;

        CanonicalLocalFolder? canonical;
        var canonicalPrefixLength = targetFolder.StartsWith(CanonicalFolderPrefix, StringComparison.Ordinal)
            ? CanonicalFolderPrefix.Length
            : targetFolder.StartsWith(PrintableCanonicalFolderPrefix, StringComparison.OrdinalIgnoreCase)
                ? PrintableCanonicalFolderPrefix.Length
                : 0;
        if (canonicalPrefixLength > 0 &&
            Guid.TryParse(targetFolder.AsSpan(canonicalPrefixLength), out var folderId))
            canonical = tree.Folders.FirstOrDefault(folder => folder.FolderId == folderId);
        else
        {
            var normalizedTarget = Helpers.FolderPathNormalizer.Normalize(targetFolder);
            canonical = tree.Folders.FirstOrDefault(folder =>
                folder.CanonicalPath.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase));
        }

        if (canonical == null)
        {
            LogService.Debug($"Rule target '{targetFolder}' has no canonical match; using it as a physical path.");
            return targetFolder;
        }
        var resolved = await _store.EnsureCanonicalFolderBindingAsync(canonical.FolderId, accountId);
        LogService.Debug($"Rule target '{targetFolder}' resolved to '{resolved}' for account {accountId}.");
        return resolved;
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
        var physicalOutMessages = allMessages.Where(m =>
            m.Direction == MessageDirection.Outgoing && IsPhysicalOutFolder(m.FolderName)).ToList();
        LogService.Debug($"ApplyRulesToExisting: {allMessages.Count} cached, {inboxMessages.Count} in Inbox, " +
            $"{physicalOutMessages.Count} in physical Out/Sent, {enabledRules.Count} enabled rules");

        foreach (var rule in enabledRules)
        {
            ct.ThrowIfCancellationRequested();

            // This is an explicitly user-invoked run. The extra Out scope is therefore honoured
            // here, but never in ApplyAutomaticRulesAsync (which only sees the newly arrived Inbox
            // batch). A rule can remain manual while still covering both directions when invoked.
            IEnumerable<MailMessageSummary> candidates = rule.AlsoFilterOutMailbox
                ? inboxMessages.Concat(physicalOutMessages)
                    .DistinctBy(m => (m.AccountId, m.FolderName, m.MessageId))
                : inboxMessages;
            var matched = candidates.Where(m =>
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

    private static bool IsPhysicalOutFolder(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return false;
        var normalized = folderName.Replace('\\', '/').TrimEnd('/');
        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];
        return leaf.Equals("Out", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Sent", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Sent Items", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Sent Mail", StringComparison.OrdinalIgnoreCase);
    }
}
