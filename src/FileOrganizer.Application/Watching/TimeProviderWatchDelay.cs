namespace FileOrganizer.Application.Watching;

/// <summary>Uses an injected clock for production debounce and stability delays.</summary>
public sealed class TimeProviderWatchDelay : IWatchDelay
{
    private readonly TimeProvider _timeProvider;

    public TimeProviderWatchDelay(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        return new ValueTask(Task.Delay(delay, _timeProvider, cancellationToken));
    }
}
