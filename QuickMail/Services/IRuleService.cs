using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public interface IRuleService
{
    /// <summary>Load all rules from rules.json. Returns empty list if file is missing or corrupted.</summary>
    List<MailRule> LoadRules();

    /// <summary>Persist all rules to rules.json. Creates the data directory if needed.</summary>
    void SaveRules(List<MailRule> rules);

    /// <summary>
    /// Creates the FROM-only Move-to-folder rule used by Ctrl+Shift+Drop, or reuses an existing
    /// equivalent global rule. Reuse updates its destination and enables Also Mark as read rather
    /// than accumulating duplicate rules.
    /// </summary>
    (MailRule Rule, bool Created) SaveOrUpdateQuickMoveRule(MailRule candidate) =>
        throw new NotSupportedException("Quick move rule upsert is not supported by this rule service.");

    /// <summary>
    /// Apply enabled rules to a batch of incoming messages for a specific account.
    /// Rules are evaluated in list order. Each rule is tested against every message;
    /// matching messages have the rule's action executed.
    /// Returns the number of messages that matched at least one rule, and the list
    /// of messages that were moved or deleted (removed from the incoming list).
    /// </summary>
    Task<(int MatchedCount, List<MailMessageSummary> RemovedMessages)> ApplyRulesAsync(
        List<MailMessageSummary> incoming,
        Guid accountId,
        CancellationToken ct);

    /// <summary>Apply only rules explicitly enabled for automatic incoming-mail processing.</summary>
    Task<(int MatchedCount, List<MailMessageSummary> RemovedMessages)> ApplyAutomaticRulesAsync(
        List<MailMessageSummary> incoming,
        Guid accountId,
        CancellationToken ct) => ApplyRulesAsync(incoming, accountId, ct);

    /// <summary>
    /// Test a rule against a set of messages without executing any actions.
    /// Returns the subset of messages that would match.
    /// </summary>
    List<MailMessageSummary> TestRule(MailRule rule, IEnumerable<MailMessageSummary> messages);

    /// <summary>Tests one message with its complete body when it is already available.</summary>
    bool IsMatch(MailRule rule, MailMessageSummary message, string? completeBody = null) =>
        TestRule(rule, [message]).Count > 0;

    /// <summary>Apply one rule and return its match count plus rows moved/deleted from the source.</summary>
    Task<(int MatchedCount, List<MailMessageSummary> RemovedMessages)> ApplyRuleToMessagesAsync(
        MailRule rule,
        List<MailMessageSummary> messages,
        ILocalStoreService store,
        CancellationToken ct);

    /// <summary>
    /// Apply all enabled rules to messages already in the local store. Every rule evaluates each
    /// account's Inbox; a rule with <see cref="MailRule.AlsoFilterOutMailbox"/> also evaluates the
    /// physical Out/Sent mailbox. Invoked only by the user-facing "Run on Existing Mail" action.
    /// <para>
    /// <paramref name="inboxFolderByAccount"/> maps an account id to the
    /// <see cref="MailFolderModel.FullName"/> of that account's Inbox. The caller supplies
    /// it because folder-kind knowledge lives in the view layer, not here — for a Graph
    /// account the Inbox's opaque id is never <c>"INBOX"</c>, so it cannot be recognised by
    /// name. Filed outgoing messages in custom folders, Archive, Junk and Trash are left untouched;
    /// the extra scope is the physical Out/Sent mailbox only. Any account missing from the map is
    /// skipped for Inbox processing (fail-closed). This option never affects automatic arrivals.
    /// </para>
    /// Returns messages that were moved or deleted (should be removed from UI).
    /// </summary>
    Task<List<MailMessageSummary>> ApplyRulesToExistingAsync(
        ILocalStoreService store,
        IReadOnlyDictionary<Guid, string> inboxFolderByAccount,
        CancellationToken ct);
}
