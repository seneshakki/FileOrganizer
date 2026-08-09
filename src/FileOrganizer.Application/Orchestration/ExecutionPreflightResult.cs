using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Orchestration;

/// <summary>An immutable read-only preflight snapshot for a complete manifest.</summary>
public sealed class ExecutionPreflightResult
{
    private readonly ReadOnlyCollection<ExecutionPreflightItemResult> _items;

    public ExecutionPreflightResult(IEnumerable<ExecutionPreflightItemResult> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        ExecutionPreflightItemResult[] snapshot = [.. items];
        foreach (ExecutionPreflightItemResult item in snapshot)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));
        }

        Array.Sort(snapshot, static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        for (int ordinal = 0; ordinal < snapshot.Length; ordinal++)
        {
            if (snapshot[ordinal].Ordinal != ordinal)
            {
                throw new ArgumentException(
                    "Preflight items must contain contiguous manifest ordinals.",
                    nameof(items));
            }
        }

        _items = Array.AsReadOnly(snapshot);
    }

    public IReadOnlyList<ExecutionPreflightItemResult> Items => _items;

    public bool Succeeded => _items.All(item => item.Readiness == ExecutionReadiness.Ready);
}
