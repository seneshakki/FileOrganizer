namespace FileOrganizer.Application.Watching;

/// <summary>Positive timing and concurrency limits for the read-only watch preview runtime.</summary>
public sealed class WatchPreviewRuntimeOptions
{
    public WatchPreviewRuntimeOptions(
        TimeSpan debounceWindow,
        TimeSpan stabilityInterval,
        int maxConcurrentScans)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(debounceWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stabilityInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentScans, 1);

        DebounceWindow = debounceWindow;
        StabilityInterval = stabilityInterval;
        MaxConcurrentScans = maxConcurrentScans;
    }

    public TimeSpan DebounceWindow { get; }

    public TimeSpan StabilityInterval { get; }

    public int MaxConcurrentScans { get; }
}
