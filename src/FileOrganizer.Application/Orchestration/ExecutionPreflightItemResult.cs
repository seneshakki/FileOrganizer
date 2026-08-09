using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Orchestration;

/// <summary>The immutable whole-plan preflight decision for one manifest item.</summary>
public sealed class ExecutionPreflightItemResult
{
    public ExecutionPreflightItemResult(
        int ordinal,
        ExecutionReadiness readiness,
        ExecutionProblem? problem = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        if (!Enum.IsDefined(readiness))
        {
            throw new ArgumentOutOfRangeException(nameof(readiness));
        }

        bool needsProblem = readiness != ExecutionReadiness.Ready;
        if (needsProblem != (problem is not null))
        {
            throw new ArgumentException(
                "Only a blocked or conflicting preflight result requires a problem.",
                nameof(problem));
        }

        if (problem is not null && problem.OperationOrdinal != ordinal)
        {
            throw new ArgumentException(
                "A preflight problem must identify its manifest operation.",
                nameof(problem));
        }

        if (readiness == ExecutionReadiness.Conflict
            && problem?.Kind != ExecutionProblemKind.DestinationConflict)
        {
            throw new ArgumentException(
                "A preflight conflict requires a destination-conflict problem.",
                nameof(problem));
        }

        if (readiness == ExecutionReadiness.Blocked
            && problem?.Kind is not (
                ExecutionProblemKind.SourceRevalidationFailed
                or ExecutionProblemKind.DestinationRevalidationFailed))
        {
            throw new ArgumentException(
                "A blocked preflight result requires a source or destination revalidation problem.",
                nameof(problem));
        }

        Ordinal = ordinal;
        Readiness = readiness;
        Problem = problem;
    }

    public int Ordinal { get; }

    public ExecutionReadiness Readiness { get; }

    public ExecutionProblem? Problem { get; }

    public static ExecutionPreflightItemResult Ready(int ordinal) =>
        new(ordinal, ExecutionReadiness.Ready);

    public static ExecutionPreflightItemResult Blocked(
        int ordinal,
        ExecutionProblem problem) =>
        new(ordinal, ExecutionReadiness.Blocked, problem);

    public static ExecutionPreflightItemResult Conflict(
        int ordinal,
        ExecutionProblem problem) =>
        new(ordinal, ExecutionReadiness.Conflict, problem);
}
