using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Orchestration;

namespace FileOrganizer.Application.Tests.Orchestration;

public sealed class MoveAttemptResultTests
{
    [Fact]
    public void SuccessHasTheExactOutcomeAndNoProblem()
    {
        MoveAttemptResult result = MoveAttemptResult.Success();

        Assert.Equal(MoveAttemptOutcome.Succeeded, result.Outcome);
        Assert.True(result.ReportedSuccess);
        Assert.Null(result.Problem);
    }

    [Theory]
    [InlineData(MoveAttemptOutcome.Blocked, ExecutionProblemKind.SourceRevalidationFailed)]
    [InlineData(MoveAttemptOutcome.Blocked, ExecutionProblemKind.DestinationRevalidationFailed)]
    [InlineData(MoveAttemptOutcome.Conflict, ExecutionProblemKind.DestinationConflict)]
    [InlineData(MoveAttemptOutcome.Failed, ExecutionProblemKind.MoveFailed)]
    public void FailureFactoriesPreserveTheExactOutcomeAndProblem(
        MoveAttemptOutcome outcome,
        ExecutionProblemKind problemKind)
    {
        ExecutionProblem problem = new(problemKind, "A structured move-attempt problem.", 0);

        MoveAttemptResult result = outcome switch
        {
            MoveAttemptOutcome.Blocked => MoveAttemptResult.Blocked(problem),
            MoveAttemptOutcome.Conflict => MoveAttemptResult.Conflict(problem),
            MoveAttemptOutcome.Failed => MoveAttemptResult.Failure(problem),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

        Assert.Equal(outcome, result.Outcome);
        Assert.False(result.ReportedSuccess);
        Assert.Same(problem, result.Problem);
    }

    [Theory]
    [InlineData(MoveAttemptOutcome.Blocked, ExecutionProblemKind.MoveFailed)]
    [InlineData(MoveAttemptOutcome.Conflict, ExecutionProblemKind.SourceRevalidationFailed)]
    [InlineData(MoveAttemptOutcome.Failed, ExecutionProblemKind.DestinationConflict)]
    public void FailureFactoriesRejectIncompatibleProblemKinds(
        MoveAttemptOutcome outcome,
        ExecutionProblemKind problemKind)
    {
        ExecutionProblem problem = new(problemKind, "An incompatible problem.", 0);

        Assert.Throws<ArgumentException>(
            () => outcome switch
            {
                MoveAttemptOutcome.Blocked => MoveAttemptResult.Blocked(problem),
                MoveAttemptOutcome.Conflict => MoveAttemptResult.Conflict(problem),
                MoveAttemptOutcome.Failed => MoveAttemptResult.Failure(problem),
                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
            });
    }
}
