using System.Collections.ObjectModel;
using FileOrganizer.Core.Collections;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.Core.Planning;

/// <summary>
/// A caller-supplied, immutable set of read-only destination observations.
/// </summary>
/// <remarks>
/// The planner never fills gaps by reading the file system. A requested path
/// absent from this snapshot is blocked rather than assumed not to exist.
/// </remarks>
public sealed class DestinationStateSnapshot
{
    private readonly ReadOnlyCollection<DestinationPathFact> _facts;

    /// <param name="pathSemantics">The explicit semantics shared by every path.</param>
    /// <param name="facts">The read-only destination observations to snapshot.</param>
    /// <param name="cancellationToken">
    /// Stops immutable materialization and ordering without returning a partial snapshot.
    /// </param>
    public DestinationStateSnapshot(
        PathSemantics pathSemantics,
        IEnumerable<DestinationPathFact> facts,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PathPolicy policy = new(
            pathSemantics.Syntax,
            pathSemantics.CaseSensitivity,
            pathSemantics.UnicodeNormalization);

        DestinationPathFact[] materialized = CancellationAwareCollection.Snapshot(
            facts,
            nameof(facts),
            cancellationToken);
        foreach (DestinationPathFact fact in materialized)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (fact.Path.Semantics != pathSemantics)
            {
                throw new ArgumentException(
                    "Every destination fact must use the snapshot's path semantics.",
                    nameof(facts));
            }
        }

        CancellationAwareCollection.Sort(
            materialized,
            new FactComparer(policy),
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        PathSemantics = pathSemantics;
        _facts = Array.AsReadOnly(materialized);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Gets the explicit lexical semantics shared by every fact.</summary>
    public PathSemantics PathSemantics { get; }

    /// <summary>Gets the facts in deterministic path and state order.</summary>
    public IReadOnlyList<DestinationPathFact> Facts => _facts;

    private sealed class FactComparer(PathPolicy policy) : IComparer<DestinationPathFact>
    {
        public int Compare(DestinationPathFact? left, DestinationPathFact? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int path = policy.DeterministicComparer.Compare(left.Path, right.Path);
            if (path != 0)
            {
                return path;
            }

            int entryKind = left.EntryKind.CompareTo(right.EntryKind);
            if (entryKind != 0)
            {
                return entryKind;
            }

            int failureKind = Nullable.Compare(left.Failure?.Kind, right.Failure?.Kind);
            return failureKind != 0
                ? failureKind
                : StringComparer.Ordinal.Compare(
                    left.Failure?.Description,
                    right.Failure?.Description);
        }
    }
}
