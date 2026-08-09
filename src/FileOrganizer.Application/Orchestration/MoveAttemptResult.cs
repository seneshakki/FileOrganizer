using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Orchestration;

/// <summary>
/// What the abstract move primitive reported. The executor always performs a
/// separate observation and treats that observation as the authoritative state.
/// </summary>
public sealed class MoveAttemptResult
{
    private MoveAttemptResult(bool reportedSuccess, ExecutionProblem? problem)
    {
        if (reportedSuccess == (problem is not null))
        {
            throw new ArgumentException(
                "A reported move failure requires exactly one structured problem.",
                nameof(problem));
        }

        if (problem is not null && problem.Kind != ExecutionProblemKind.MoveFailed)
        {
            throw new ArgumentException(
                "A move-attempt failure requires a move-failed problem.",
                nameof(problem));
        }

        ReportedSuccess = reportedSuccess;
        Problem = problem;
    }

    public bool ReportedSuccess { get; }

    public ExecutionProblem? Problem { get; }

    public static MoveAttemptResult Success() => new(reportedSuccess: true, null);

    public static MoveAttemptResult Failure(ExecutionProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new(reportedSuccess: false, problem);
    }
}
