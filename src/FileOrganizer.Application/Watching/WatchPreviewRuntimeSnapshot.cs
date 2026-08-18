using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Watching;

/// <summary>An immutable configured-order snapshot of all independent watch-root previews.</summary>
/// <remarks>
/// Roots remain separate. This type deliberately exposes no aggregate eligibility or execution
/// decision because each preview is historical and independently fenced.
/// </remarks>
public sealed class WatchPreviewRuntimeSnapshot
{
    private readonly ReadOnlyCollection<WatchPreviewRootSnapshot> _roots;

    public WatchPreviewRuntimeSnapshot(
        long revision,
        IEnumerable<WatchPreviewRootSnapshot> roots)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentNullException.ThrowIfNull(roots);
        WatchPreviewRootSnapshot[] snapshot = [.. roots];
        if (snapshot.Any(static root => root is null))
        {
            throw new ArgumentException("Runtime roots cannot contain null.", nameof(roots));
        }

        if (snapshot.Select(static root => root.WatchRootId).Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException("Runtime root IDs must be unique.", nameof(roots));
        }

        Revision = revision;
        _roots = Array.AsReadOnly(snapshot);
    }

    public long Revision { get; }

    /// <summary>Gets roots in immutable configuration order.</summary>
    public IReadOnlyList<WatchPreviewRootSnapshot> Roots => _roots;
}
