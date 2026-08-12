using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Orchestration;

/// <summary>
/// What the abstract move primitive reported. The executor always reconciles
/// this report with a fresh physical observation: proof of an applied,
/// conflicting, or ambiguous physical state takes precedence, while proof that
/// the source remained unmoved preserves a reported block or target conflict.
/// </summary>
public sealed class MoveAttemptResult
{
    private MoveAttemptResult(MoveAttemptOutcome outcome, ExecutionProblem? problem)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        bool succeeded = outcome == MoveAttemptOutcome.Succeeded;
        if (succeeded == (problem is not null))
        {
            throw new ArgumentException(
                "A successful move attempt requires no problem; every other outcome requires one.",
                nameof(problem));
        }

        bool problemMatchesOutcome = outcome switch
        {
            MoveAttemptOutcome.Succeeded => true,
            MoveAttemptOutcome.Blocked => problem!.Kind is
                ExecutionProblemKind.SourceRevalidationFailed
                or ExecutionProblemKind.DestinationRevalidationFailed,
            MoveAttemptOutcome.Conflict =>
                problem!.Kind == ExecutionProblemKind.DestinationConflict,
            MoveAttemptOutcome.Failed => problem!.Kind == ExecutionProblemKind.MoveFailed,
            _ => false,
        };
        if (!problemMatchesOutcome)
        {
            throw new ArgumentException(
                "The move-attempt problem is incompatible with its outcome.",
                nameof(problem));
        }

        Outcome = outcome;
        Problem = problem;
    }

    public MoveAttemptOutcome Outcome { get; }

    public bool ReportedSuccess => Outcome == MoveAttemptOutcome.Succeeded;

    public ExecutionProblem? Problem { get; }

    public static MoveAttemptResult Success() => new(MoveAttemptOutcome.Succeeded, null);

    public static MoveAttemptResult Blocked(ExecutionProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new(MoveAttemptOutcome.Blocked, problem);
    }

    public static MoveAttemptResult Conflict(ExecutionProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new(MoveAttemptOutcome.Conflict, problem);
    }

    public static MoveAttemptResult Failure(ExecutionProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new(MoveAttemptOutcome.Failed, problem);
    }
}
