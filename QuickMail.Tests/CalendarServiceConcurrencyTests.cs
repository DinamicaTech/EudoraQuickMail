using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.Tests;

public sealed class CalendarServiceConcurrencyTests
{
    [Fact]
    public async Task ConcurrentRebuildCallersJoinOneActiveReload()
    {
        var provider = new GatedCalendarProvider();
        var service = new CalendarService(provider);

        var first = service.RebuildAsync();
        await provider.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.RebuildAsync();

        Assert.Same(first, second);
        provider.AllowLoad.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, provider.LoadCalls);
    }

    private sealed class GatedCalendarProvider : ICalendarProvider
    {
        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowLoad { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int LoadCalls { get; private set; }

        public async Task<List<CalendarEvent>> LoadEventsAsync(CancellationToken ct = default)
        {
            LoadCalls++;
            LoadStarted.TrySetResult();
            await AllowLoad.Task.WaitAsync(ct);
            return [];
        }

        public Task UpsertEventAsync(CalendarEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateResponseStatusAsync(string uid, Guid accountId,
            CalendarResponseStatus status, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteEventAsync(string uid, Guid accountId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
