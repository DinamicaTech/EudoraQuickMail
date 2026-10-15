using QuickMail.Models;

namespace QuickMail.Services;

public static class RulePreviewBuilder
{
    public static RulePreviewResult Build(MailRule rule, IReadOnlyList<MailMessageSummary> messages,
        IReadOnlyList<MailMessageSummary> matches, string scope)
    {
        var action = rule.Action switch
        {
            RuleAction.MoveToFolder => $"Move to {rule.TargetFolder ?? "(no folder)"}.",
            RuleAction.Delete => "Move to Trash.",
            RuleAction.MarkAsRead => "Mark as read.",
            RuleAction.MarkAsUnread => "Mark as unread.",
            RuleAction.Forward => $"Queue forwarding to {rule.ForwardTo}; retain originals.",
            RuleAction.ReplyTemplate => "Queue template replies to eligible incoming senders; retain originals.",
            _ => "No action."
        };
        if (rule.AlsoMarkAsRead && rule.Action != RuleAction.MarkAsRead) action += " Also mark as read.";
        if (rule.Action is RuleAction.Forward or RuleAction.ReplyTemplate)
            action += " Counts are condition matches, not guaranteed sends: deduplication and loop/bulk protections are checked at execution.";
        if (!rule.IsEnabled) action = "Rule is disabled; batch execution skips it. Preview of conditions only. " + action;
        if (rule.IsExpired) action = $"Expired on {rule.ActiveUntil:d}. No messages will be processed.";
        else if (rule.ActiveUntil.HasValue) action += $" Active through {rule.ActiveUntil:d} inclusive.";
        return new(rule.Name, scope, action, messages.Count, matches.Count,
            matches.Count(m => m.Direction == MessageDirection.Incoming), matches.Count(m => m.Direction == MessageDirection.Outgoing),
            rule.AlsoMarkAsRead || rule.Action == RuleAction.MarkAsRead ? matches.Count : 0,
            matches.OrderByDescending(m => m.Date).Take(20).ToList());
    }
}
