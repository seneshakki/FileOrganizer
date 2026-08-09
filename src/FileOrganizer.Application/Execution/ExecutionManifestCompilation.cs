using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Execution;

/// <summary>The immutable success or structured rejection from manifest compilation.</summary>
public sealed class ExecutionManifestCompilation
{
    private readonly ReadOnlyCollection<ExecutionManifestProblem> _problems;

    private ExecutionManifestCompilation(
        ExecutionManifest? manifest,
        IEnumerable<ExecutionManifestProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        ExecutionManifestProblem[] snapshot = [.. problems];
        foreach (ExecutionManifestProblem problem in snapshot)
        {
            ArgumentNullException.ThrowIfNull(problem, nameof(problems));
        }

        Array.Sort(snapshot, ManifestProblemComparer.Instance);

        if ((manifest is null) == (snapshot.Length == 0))
        {
            throw new ArgumentException(
                "A successful compilation has one manifest and no problems; a rejection has problems and no manifest.",
                nameof(problems));
        }

        Manifest = manifest;
        _problems = Array.AsReadOnly(snapshot);
    }

    /// <summary>Gets whether a manifest was compiled.</summary>
    public bool Succeeded => Manifest is not null;

    /// <summary>Gets the immutable manifest, or <see langword="null"/> when rejected.</summary>
    public ExecutionManifest? Manifest { get; }

    /// <summary>Gets deterministic structured rejection reasons.</summary>
    public IReadOnlyList<ExecutionManifestProblem> Problems => _problems;

    internal static ExecutionManifestCompilation Success(ExecutionManifest manifest) =>
        new(manifest, []);

    internal static ExecutionManifestCompilation Rejected(
        IEnumerable<ExecutionManifestProblem> problems) =>
        new(null, problems);

    private sealed class ManifestProblemComparer : IComparer<ExecutionManifestProblem>
    {
        internal static ManifestProblemComparer Instance { get; } = new();

        public int Compare(ExecutionManifestProblem? left, ExecutionManifestProblem? right)
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

            int source = StringComparer.Ordinal.Compare(
                left.AffectedSource?.Value,
                right.AffectedSource?.Value);
            if (source != 0)
            {
                return source;
            }

            int kind = left.Kind.CompareTo(right.Kind);
            if (kind != 0)
            {
                return kind;
            }

            int action = Nullable.Compare(left.ActionKind, right.ActionKind);
            return action != 0
                ? action
                : StringComparer.Ordinal.Compare(left.Description, right.Description);
        }
    }
}
