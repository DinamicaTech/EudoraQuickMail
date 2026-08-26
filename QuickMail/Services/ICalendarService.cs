using System;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Application-level calendar operations: load/rebuild events with
/// filtering/sorting, and update response status. Delegates persistence to
/// <see cref="ICalendarProvider"/>.
/// </summary>
public interface ICalendarService
{
    /// <summary>Reloads the already-materialized events without scanning cached messages.</summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>Re-harvests calendar invitations from cached messages and reloads the list.</summary>
    Task RebuildAsync(CancellationToken ct = default);

    /// <summary>All events, sorted by start time ascending (nulls last).</summary>
    IReadOnlyList<CalendarEvent> Events { get; }

    /// <summary>
    /// Inserts or updates a single event (upsert by Uid + AccountId) and refreshes
    /// the in-memory list. Used when an invite reply creates or updates an event
    /// immediately, without waiting for the next harvest.
    /// </summary>
    Task UpsertEventAsync(CalendarEvent evt, CancellationToken ct = default);

    /// <summary>Updates the response status for an event and persists it.</summary>
    Task SetResponseStatusAsync(string uid, Guid accountId, CalendarResponseStatus status, CancellationToken ct = default);

    /// <summary>Deletes an event (by Uid + AccountId) and refreshes the in-memory list.</summary>
    Task DeleteEventAsync(string uid, Guid accountId, CancellationToken ct = default);
}
