using System.Collections.ObjectModel;
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

    public DestinationStateSnapshot(
        PathSemantics pathSemantics,
        IEnumerable<DestinationPathFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        PathPolicy policy = new(
            pathSemantics.Syntax,
            pathSemantics.CaseSensitivity,
            pathSemantics.UnicodeNormalization);

        DestinationPathFact[] materialized = [.. facts];
        foreach (DestinationPathFact fact in materialized)
        {
            ArgumentNullException.ThrowIfNull(fact, nameof(facts));

            if (fact.Path.Semantics != pathSemantics)
            {
                throw new ArgumentException(
                    "Every destination fact must use the snapshot's path semantics.",
                    nameof(facts));
            }
        }

        Array.Sort(materialized, new FactComparer(policy));

        PathSemantics = pathSemantics;
        _facts = Array.AsReadOnly(materialized);
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
