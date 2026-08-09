namespace FileOrganizer.Application.Execution;

internal sealed class ExecutionProblemComparer : IComparer<ExecutionProblem>
{
    internal static ExecutionProblemComparer Instance { get; } = new();

    public int Compare(ExecutionProblem? left, ExecutionProblem? right)
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

        int ordinal = Nullable.Compare(left.OperationOrdinal, right.OperationOrdinal);
        if (ordinal != 0)
        {
            return ordinal;
        }

        int kind = left.Kind.CompareTo(right.Kind);
        return kind != 0
            ? kind
            : StringComparer.Ordinal.Compare(left.Description, right.Description);
    }
}
