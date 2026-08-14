namespace FileOrganizer.Application.Watching;

/// <summary>Identifies the event generation at which a full baseline scan began.</summary>
public sealed record WatchBaselineToken
{
    public WatchBaselineToken(Guid watchRootId, long eventGeneration)
    {
        if (watchRootId == Guid.Empty)
        {
            throw new ArgumentException("A baseline token requires a watch-root ID.", nameof(watchRootId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(eventGeneration);
        WatchRootId = watchRootId;
        EventGeneration = eventGeneration;
    }

    public Guid WatchRootId { get; }

    public long EventGeneration { get; }
}
