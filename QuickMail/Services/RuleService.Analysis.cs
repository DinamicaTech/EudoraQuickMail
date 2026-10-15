using System.Text;
using System.Text.Json;
using QuickMail.Models;

namespace QuickMail.Services;

public partial class RuleService
{
    public async Task<string> AnalyzeConflictsAsync(IReadOnlyList<MailMessageSummary> messages,
        CancellationToken ct = default)
    {
        var rules = LoadRules().Where(r => r.IsEnabled && !r.IsExpired).OrderByDescending(r => r.IsSpamFilter).ToList();
        var needsDetails = rules.Any(r => r.UseBodyCondition || r.UseToCondition && r.AlsoCcBcc);
        var data = needsDetails ? await LoadRuleMatchDataAsync(_store, messages, ct) : [];
        var report = new StringBuilder("RULE CONFLICT ANALYSIS — selected messages\r\n\r\n");
        report.AppendLine($"{messages.Count:N0} selected messages; {rules.Count:N0} active rules. SPAM rules first, then saved list order.");
        report.AppendLine("Read-only manual-batch simulation. No messages are moved, changed or sent. Disabled/expired rules excluded.");
        report.AppendLine("Later matches after a move/delete or SPAM rule are shown as blocked, assuming successful actions. Forward/reply protections and delivery are not simulated.");
        if (needsDetails && messages.Any(m => !data.ContainsKey((m.AccountId,m.FolderName,m.MessageId))))
            report.AppendLine("WARNING: full cached body/CC/BCC is unavailable for some messages; matches based on summary data may be incomplete.");
        var duplicates = rules.GroupBy(ConditionAndActionKey).Where(g => g.Count() > 1).ToList();
        foreach (var group in duplicates)
            report.AppendLine("Potential duplicate rules (same conditions/action): " + string.Join("; ", group.Select(r => r.Name)));
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            data.TryGetValue((message.AccountId,message.FolderName,message.MessageId), out var detail);
            var matching = rules.Select((rule,index) => (Rule: rule, Order: index + 1))
                .Where(x => (!x.Rule.AccountId.HasValue || x.Rule.AccountId == message.AccountId) &&
                    MatchesRule(x.Rule, message, detail?.Body, detail?.Cc, detail?.Bcc)).ToList();
            report.AppendLine($"\r\n{message.Date.LocalDateTime:g} — {message.Subject}\r\n{message.From} | {message.FolderName}");
            if (matching.Count == 0) { report.AppendLine("No matching active rules."); continue; }
            if (matching.Where(x => x.Rule.Action == RuleAction.MoveToFolder).Select(x => x.Rule.TargetFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                report.AppendLine("WARNING: competing destination folders.");
            if (matching.Any(x => x.Rule.Action == RuleAction.Delete) && matching.Any(x => x.Rule.Action is RuleAction.Forward or RuleAction.ReplyTemplate))
                report.AppendLine("WARNING: deletion combined with forwarding/automatic reply; inspect execution order.");
            if (matching.Any(x => x.Rule.Action == RuleAction.MarkAsUnread) && matching.Any(x => x.Rule.Action == RuleAction.MarkAsRead || x.Rule.AlsoMarkAsRead))
                report.AppendLine("WARNING: competing read/unread actions.");
            string? blockedBy = null;
            foreach (var match in matching)
            {
                var action = RulePreviewBuilder.Build(match.Rule, [message], [message], "Selection").ActionDescription;
                report.AppendLine($"  {match.Order}. {match.Rule.Name}: {action}" + (blockedBy == null ? "" : $" [BLOCKED by {blockedBy}]") +
                    (match.Rule.ApplyAutomatically ? " [automatic + manual]" : " [manual only]"));
                if (blockedBy == null && (match.Rule.IsSpamFilter || match.Rule.Action is RuleAction.MoveToFolder or RuleAction.Delete)) blockedBy = match.Rule.Name;
            }
        }
        return report.ToString();
    }

    private static string ConditionAndActionKey(MailRule r) => JsonSerializer.Serialize(new
    {
        r.AccountId, r.IsSpamFilter, r.SpamBlockDomain,
        From = r.UseFromCondition ? r.FromContains?.Trim().ToUpperInvariant() : null,
        To = r.UseToCondition ? r.ToContains?.Trim().ToUpperInvariant() : null,
        Cc = r.UseToCondition && r.AlsoCcBcc,
        Subject = r.UseSubjectCondition ? r.SubjectContains?.Trim().ToUpperInvariant() : null,
        Body = r.UseBodyCondition ? r.BodyContains?.Trim().ToUpperInvariant() : null,
        r.MustHaveAttachments, r.Action, r.TargetFolder, r.ForwardTo, r.ReplyTemplateId, r.AlsoMarkAsRead,
        r.ApplyAutomatically, r.AlsoFilterOutMailbox, r.ActiveUntil
    });
}
