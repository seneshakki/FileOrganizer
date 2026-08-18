using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Watching;

/// <summary>One coalesced aggregate snapshot and every root changed since the prior publication.</summary>
public sealed class WatchPreviewRuntimeNotification
{
    private readonly ReadOnlyCollection<Guid> _changedRootIds;

    public WatchPreviewRuntimeNotification(
        WatchPreviewRuntimeSnapshot snapshot,
        IEnumerable<Guid> changedRootIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(changedRootIds);
        Guid[] ids = [.. changedRootIds];
        if (ids.Length == 0 || ids.Any(static id => id == Guid.Empty))
        {
            throw new ArgumentException(
                "A runtime notification requires at least one non-empty changed root ID.",
                nameof(changedRootIds));
        }

        if (ids.Distinct().Count() != ids.Length)
        {
            throw new ArgumentException("Changed root IDs must be unique.", nameof(changedRootIds));
        }

        HashSet<Guid> snapshotIds = snapshot.Roots
            .Select(static root => root.WatchRootId)
            .ToHashSet();
        if (ids.Any(id => !snapshotIds.Contains(id)))
        {
            throw new ArgumentException(
                "Every changed root ID must belong to the aggregate snapshot.",
                nameof(changedRootIds));
        }

        HashSet<Guid> changedIds = ids.ToHashSet();
        Guid[] configuredOrder = snapshot.Roots
            .Where(root => changedIds.Contains(root.WatchRootId))
            .Select(static root => root.WatchRootId)
            .ToArray();
        if (!ids.SequenceEqual(configuredOrder))
        {
            throw new ArgumentException(
                "Changed root IDs must follow aggregate snapshot order.",
                nameof(changedRootIds));
        }

        Snapshot = snapshot;
        _changedRootIds = Array.AsReadOnly(ids);
    }

    public WatchPreviewRuntimeSnapshot Snapshot { get; }

    /// <summary>Gets changed root IDs in configured order.</summary>
    public IReadOnlyList<Guid> ChangedRootIds => _changedRootIds;
}
