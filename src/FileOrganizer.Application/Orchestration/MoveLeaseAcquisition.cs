using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Orchestration;

/// <summary>The immediate source/destination revalidation result for one move.</summary>
public sealed class MoveLeaseAcquisition
{
    private MoveLeaseAcquisition(
        ExecutionReadiness readiness,
        IMoveOperationLease? lease,
        ExecutionProblem? problem)
    {
        if (!Enum.IsDefined(readiness))
        {
            throw new ArgumentOutOfRangeException(nameof(readiness));
        }

        bool ready = readiness == ExecutionReadiness.Ready;
        if (ready != (lease is not null) || ready == (problem is not null))
        {
            throw new ArgumentException(
                "A ready acquisition requires only a lease; a failed acquisition requires only a problem.",
                nameof(lease));
        }

        if (readiness == ExecutionReadiness.Conflict
            && problem?.Kind != ExecutionProblemKind.DestinationConflict)
        {
            throw new ArgumentException(
                "A move conflict requires a destination-conflict problem.",
                nameof(problem));
        }

        if (readiness == ExecutionReadiness.Blocked
            && problem?.Kind is not (
                ExecutionProblemKind.SourceRevalidationFailed
                or ExecutionProblemKind.DestinationRevalidationFailed))
        {
            throw new ArgumentException(
                "A blocked move requires a source or destination revalidation problem.",
                nameof(problem));
        }

        Readiness = readiness;
        Lease = lease;
        Problem = problem;
    }

    public ExecutionReadiness Readiness { get; }

    public IMoveOperationLease? Lease { get; }

    public ExecutionProblem? Problem { get; }

    public static MoveLeaseAcquisition Ready(IMoveOperationLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new(ExecutionReadiness.Ready, lease, null);
    }

    public static MoveLeaseAcquisition Blocked(ExecutionProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new(ExecutionReadiness.Blocked, null, problem);
    }

    public static MoveLeaseAcquisition Conflict(ExecutionProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new(ExecutionReadiness.Conflict, null, problem);
    }
}
