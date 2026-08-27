using QuickMail.Models;

namespace QuickMail.Helpers;

public readonly record struct MessageListIdentity(Guid AccountId, string FolderName, string MessageId)
{
    public static MessageListIdentity From(MailMessageSummary message) =>
        new(message.AccountId, message.FolderName.ToUpperInvariant(), message.MessageId);
}

public sealed record MessageListContinuation(int FallbackIndex, IReadOnlyList<MessageListIdentity> Candidates);

/// <summary>
/// Preserves the user's logical row after a filter removes messages and reloads the collection.
/// The first surviving row at or after the original position wins; at the end of the list the
/// closest preceding row is selected, matching Delete/Shift+Delete behavior.
/// </summary>
public static class MessageListContinuationPolicy
{
    public static MessageListContinuation Capture(
        IReadOnlyList<MailMessageSummary> visible,
        IReadOnlyCollection<MailMessageSummary> affected,
        int selectedIndex)
    {
        var affectedKeys = affected.Select(MessageListIdentity.From).ToHashSet();
        var affectedIndices = visible
            .Select((message, index) => (Key: MessageListIdentity.From(message), Index: index))
            .Where(item => affectedKeys.Contains(item.Key))
            .Select(item => item.Index)
            .ToList();
        var index = affectedIndices.Count > 0 ? affectedIndices.Min() : selectedIndex;
        index = visible.Count == 0 ? 0 : Math.Clamp(index, 0, visible.Count - 1);
        return new MessageListContinuation(index,
            visible.Skip(index).Select(MessageListIdentity.From).ToList());
    }

    public static int Resolve(MessageListContinuation continuation,
        IReadOnlyList<MailMessageSummary> refreshed)
    {
        if (refreshed.Count == 0) return -1;
        var indices = refreshed
            .Select((message, index) => (Key: MessageListIdentity.From(message), Index: index))
            .GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => group.First().Index);
        foreach (var candidate in continuation.Candidates)
            if (indices.TryGetValue(candidate, out var index)) return index;
        return Math.Clamp(continuation.FallbackIndex, 0, refreshed.Count - 1);
    }
}
