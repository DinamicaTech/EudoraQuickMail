using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Durable outgoing-mail queue used by both Send and Send Later.  A successful enqueue means the
/// editor may close: transport, Sent-copy persistence and draft cleanup happen in the dispatcher.
/// </summary>
public interface IOutgoingMailQueue
{
    Task<Guid> QueueImmediateAsync(ComposeModel message, Guid? replaceScheduledId = null,
        DateTimeOffset? notBeforeUtc = null, CancellationToken ct = default);

    Task<Guid> ScheduleAsync(ComposeModel message, DateTimeOffset sendAtUtc,
        CancellationToken ct = default);

    Task ReplaceAsync(Guid id, ComposeModel message, DateTimeOffset sendAtUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels a queued Send before transport starts and materializes its latest content in Draft.
    /// Returns null when the item no longer exists or SMTP dispatch has already claimed it.
    /// </summary>
    Task<ComposeModel?> UndoSendAsync(Guid id, CancellationToken ct = default);
}
