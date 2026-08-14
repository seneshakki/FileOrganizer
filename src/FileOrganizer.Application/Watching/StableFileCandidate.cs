using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Watching;

/// <summary>A regular file proven unchanged across the configured stability interval.</summary>
public sealed record StableFileCandidate
{
    public StableFileCandidate(
        Guid watchRootId,
        CanonicalPath path,
        PhysicalEntryIdentity identity,
        PhysicalFileMetadata metadata,
        long generation,
        DateTimeOffset observedAtUtc)
    {
        if (watchRootId == Guid.Empty)
        {
            throw new ArgumentException("A stable candidate requires a watch-root ID.", nameof(watchRootId));
        }

        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(metadata);
        if (path.IsRoot)
        {
            throw new ArgumentException("A stable file candidate must include a file name.", nameof(path));
        }

        if (identity.EntryKind != PhysicalEntryKind.RegularFile)
        {
            throw new ArgumentException(
                "A stable file candidate requires regular-file identity.",
                nameof(identity));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        WatchRootId = watchRootId;
        Path = path;
        Identity = identity;
        Metadata = metadata;
        Generation = generation;
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
    }

    public Guid WatchRootId { get; }

    public CanonicalPath Path { get; }

    public PhysicalEntryIdentity Identity { get; }

    public PhysicalFileMetadata Metadata { get; }

    /// <summary>Gets the per-path generation that this candidate proves stable.</summary>
    public long Generation { get; }

    public DateTimeOffset ObservedAtUtc { get; }
}
