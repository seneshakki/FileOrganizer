namespace FileOrganizer.Application.Watching;

/// <summary>
/// Identifies one synchronized watch-root event generation for a read-only snapshot.
/// </summary>
public sealed record WatchSnapshotToken
{
    public WatchSnapshotToken(
        Guid watchRootId,
        long eventGeneration,
        long synchronizationEpoch)
    {
        if (watchRootId == Guid.Empty)
        {
            throw new ArgumentException("A watch snapshot token requires a watch-root ID.", nameof(watchRootId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(eventGeneration);
        ArgumentOutOfRangeException.ThrowIfNegative(synchronizationEpoch);
        WatchRootId = watchRootId;
        EventGeneration = eventGeneration;
        SynchronizationEpoch = synchronizationEpoch;
    }

    public Guid WatchRootId { get; }

    public long EventGeneration { get; }

    /// <summary>
    /// Gets the monitor pipeline epoch that established this synchronized snapshot.
    /// </summary>
    public long SynchronizationEpoch { get; }
}
