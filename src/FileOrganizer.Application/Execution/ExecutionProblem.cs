namespace FileOrganizer.Application.Execution;

/// <summary>A structured, immutable execution problem.</summary>
public sealed record ExecutionProblem
{
    public ExecutionProblem(
        ExecutionProblemKind kind,
        string description,
        int? operationOrdinal = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (operationOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operationOrdinal));
        }

        Kind = kind;
        Description = description;
        OperationOrdinal = operationOrdinal;
    }

    /// <summary>Gets the stable problem category.</summary>
    public ExecutionProblemKind Kind { get; }

    /// <summary>Gets a concise audit description.</summary>
    public string Description { get; }

    /// <summary>Gets the affected manifest ordinal, or null for a run-level problem.</summary>
    public int? OperationOrdinal { get; }
}
