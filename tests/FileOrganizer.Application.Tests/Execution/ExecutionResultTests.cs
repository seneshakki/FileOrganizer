using FileOrganizer.Application.Execution;
using FileOrganizer.Core.Actions;

namespace FileOrganizer.Application.Tests.Execution;

public sealed class ExecutionResultTests
{
    private static readonly DateTimeOffset StartedAt = ManifestTestPlan.GeneratedAt.AddMinutes(1);
    private static readonly DateTimeOffset FinishedAt = StartedAt.AddSeconds(2);

    [Fact]
    public void SnapshotsCallerCollectionsAndOrdersItemResultsByManifestOrdinal()
    {
        ExecutionManifest manifest = ManifestWithTwoMoves();
        List<ExecutionProblem> firstProblems =
        [
            new ExecutionProblem(
                ExecutionProblemKind.MoveFailed,
                "The scripted move failed.",
                operationOrdinal: 1),
        ];
        List<ExecutionItemResult> itemResults =
        [
            new ExecutionItemResult(1, ExecutionItemOutcome.NotApplied, firstProblems),
            new ExecutionItemResult(0, ExecutionItemOutcome.Applied, []),
        ];
        List<ExecutionProblem> runProblems = [firstProblems[0]];

        ExecutionResult result = new(
            manifest,
            StartedAt,
            FinishedAt,
            ExecutionOutcome.PartiallyCompleted,
            itemResults,
            runProblems);

        itemResults.Clear();
        firstProblems.Clear();
        runProblems.Clear();

        Assert.Equal([0, 1], result.Items.Select(item => item.Ordinal));
        Assert.Single(result.Items[1].Problems);
        Assert.Single(result.Problems);
        Assert.Throws<NotSupportedException>(() => ((IList<ExecutionItemResult>)result.Items).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ExecutionProblem>)result.Problems).Clear());
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionProblem>)result.Items[1].Problems).Clear());
    }

    [Fact]
    public void RepresentsAnIdempotentlyReplayedCompletedJournalExplicitly()
    {
        ExecutionManifest manifest = ManifestWithTwoMoves();
        ExecutionResult result = new(
            manifest,
            StartedAt,
            FinishedAt,
            ExecutionOutcome.Completed,
            [
                new ExecutionItemResult(0, ExecutionItemOutcome.Applied, []),
                new ExecutionItemResult(1, ExecutionItemOutcome.Applied, []),
            ],
            [],
            wasReplayed: true);

        Assert.Equal(manifest.Fingerprint, result.Fingerprint);
        Assert.True(result.WasReplayed);
        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
    }

    [Fact]
    public void RepresentsManifestRejectionWithoutInventingAFingerprint()
    {
        List<ExecutionManifestProblem> manifestProblems =
        [
            new ExecutionManifestProblem(
                ExecutionManifestProblemKind.PlanNotEligible,
                "The plan was incomplete."),
        ];

        ExecutionResult result = new(
            manifest: null,
            StartedAt,
            FinishedAt,
            ExecutionOutcome.Rejected,
            items: [],
            problems:
            [
                new ExecutionProblem(
                    ExecutionProblemKind.ManifestRejected,
                    "Manifest compilation rejected the plan."),
            ],
            manifestProblems);

        manifestProblems.Clear();

        Assert.Null(result.Manifest);
        Assert.Null(result.Fingerprint);
        Assert.Single(result.ManifestProblems);
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionManifestProblem>)result.ManifestProblems).Clear());
    }

    [Fact]
    public void RejectsACompletedResultThatDoesNotReportEveryOperationAsApplied()
    {
        ExecutionManifest manifest = ManifestWithTwoMoves();
        ExecutionProblem moveFailure = new(
            ExecutionProblemKind.MoveFailed,
            "The move definitely failed.",
            operationOrdinal: 1);

        Assert.Throws<ArgumentException>(
            () => new ExecutionResult(
                manifest,
                StartedAt,
                FinishedAt,
                ExecutionOutcome.Completed,
                [
                    new ExecutionItemResult(0, ExecutionItemOutcome.Applied, []),
                    new ExecutionItemResult(
                        1,
                        ExecutionItemOutcome.NotApplied,
                        [moveFailure]),
                ],
                [moveFailure]));
    }

    [Fact]
    public void RejectsAResultThatDoesNotAccountForEveryManifestOrdinal()
    {
        ExecutionManifest manifest = ManifestWithTwoMoves();
        ExecutionProblem blocked = new(
            ExecutionProblemKind.SourceRevalidationFailed,
            "The source changed after planning.",
            operationOrdinal: 0);

        Assert.Throws<ArgumentException>(
            () => new ExecutionResult(
                manifest,
                StartedAt,
                FinishedAt,
                ExecutionOutcome.Stopped,
                [new ExecutionItemResult(0, ExecutionItemOutcome.Blocked, [blocked])],
                [blocked]));
    }

    [Fact]
    public void RejectsTopLevelOutcomesThatContradictItemStates()
    {
        ExecutionManifest manifest = ManifestWithTwoMoves();
        ExecutionItemResult applied = new(0, ExecutionItemOutcome.Applied, []);
        ExecutionProblem moveFailure = new(
            ExecutionProblemKind.MoveFailed,
            "The move definitely failed.",
            operationOrdinal: 1);
        ExecutionItemResult notApplied = new(
            1,
            ExecutionItemOutcome.NotApplied,
            [moveFailure]);

        Assert.Throws<ArgumentException>(
            () => new ExecutionResult(
                manifest,
                StartedAt,
                FinishedAt,
                ExecutionOutcome.Stopped,
                [applied, notApplied],
                [moveFailure]));
        Assert.Throws<ArgumentException>(
            () => new ExecutionResult(
                manifest,
                StartedAt,
                FinishedAt,
                ExecutionOutcome.PartiallyCompleted,
                [applied, new ExecutionItemResult(1, ExecutionItemOutcome.Applied, [])],
                []));
        Assert.Throws<ArgumentException>(
            () => new ExecutionResult(
                manifest,
                StartedAt,
                FinishedAt,
                ExecutionOutcome.RecoveryRequired,
                [applied, notApplied],
                [moveFailure]));
    }

    [Theory]
    [InlineData(ExecutionItemOutcome.NotApplied)]
    [InlineData(ExecutionItemOutcome.Blocked)]
    [InlineData(ExecutionItemOutcome.Conflict)]
    [InlineData(ExecutionItemOutcome.Ambiguous)]
    public void RejectsFailureItemOutcomesWithoutAMatchingStructuredProblem(
        ExecutionItemOutcome outcome)
    {
        Assert.Throws<ArgumentException>(
            () => new ExecutionItemResult(0, outcome, []));
    }

    [Fact]
    public void RejectsAContradictoryExtraProblemEvenWhenTheOutcomeHasAMatchingProblem()
    {
        ExecutionProblem moveFailure = new(
            ExecutionProblemKind.MoveFailed,
            "The move failed.",
            operationOrdinal: 0);
        ExecutionProblem destinationConflict = new(
            ExecutionProblemKind.DestinationConflict,
            "A conflict is not a not-applied move failure.",
            operationOrdinal: 0);

        Assert.Throws<ArgumentException>(
            () => new ExecutionItemResult(
                0,
                ExecutionItemOutcome.NotApplied,
                [moveFailure, destinationConflict]));
    }

    [Theory]
    [InlineData(ExecutionItemOutcome.Applied)]
    [InlineData(ExecutionItemOutcome.NotAttempted)]
    public void RejectsNonFailureItemOutcomesWithFailureProblems(
        ExecutionItemOutcome outcome)
    {
        ExecutionProblem problem = new(
            ExecutionProblemKind.MoveFailed,
            "A failure cannot accompany this outcome.",
            operationOrdinal: 0);

        Assert.Throws<ArgumentException>(
            () => new ExecutionItemResult(0, outcome, [problem]));
    }

    [Fact]
    public void RejectsAnItemProblemThatDoesNotIdentifyThatExactOperation()
    {
        ExecutionProblem runLevel = new(
            ExecutionProblemKind.MoveFailed,
            "The ordinal is missing.");
        ExecutionProblem differentItem = new(
            ExecutionProblemKind.MoveFailed,
            "The ordinal identifies another item.",
            operationOrdinal: 1);

        Assert.Throws<ArgumentException>(
            () => new ExecutionItemResult(
                0,
                ExecutionItemOutcome.NotApplied,
                [runLevel]));
        Assert.Throws<ArgumentException>(
            () => new ExecutionItemResult(
                0,
                ExecutionItemOutcome.NotApplied,
                [differentItem]));
    }

    [Fact]
    public void RejectsAnAppliedOperationAfterAnEarlierDefiniteFailure()
    {
        ExecutionManifest manifest = ManifestWithTwoMoves();
        ExecutionProblem failure = new(
            ExecutionProblemKind.MoveFailed,
            "The first operation failed.",
            operationOrdinal: 0);

        Assert.Throws<ArgumentException>(
            () => new ExecutionResult(
                manifest,
                StartedAt,
                FinishedAt,
                ExecutionOutcome.PartiallyCompleted,
                [
                    new ExecutionItemResult(
                        0,
                        ExecutionItemOutcome.NotApplied,
                        [failure]),
                    new ExecutionItemResult(1, ExecutionItemOutcome.Applied, []),
                ],
                [failure]));
    }

    [Fact]
    public void RepresentsAWholeManifestPreflightFailureAtALaterOrdinal()
    {
        ExecutionManifest manifest = ManifestWithTwoMoves();
        ExecutionProblem conflict = new(
            ExecutionProblemKind.DestinationConflict,
            "Whole-manifest preflight found an existing target.",
            operationOrdinal: 1);

        ExecutionResult result = new(
            manifest,
            StartedAt,
            FinishedAt,
            ExecutionOutcome.Stopped,
            [
                new ExecutionItemResult(0, ExecutionItemOutcome.NotAttempted, []),
                new ExecutionItemResult(
                    1,
                    ExecutionItemOutcome.Conflict,
                    [conflict]),
            ],
            [conflict]);

        Assert.Equal(ExecutionItemOutcome.NotAttempted, result.Items[0].Outcome);
        Assert.Equal(ExecutionItemOutcome.Conflict, result.Items[1].Outcome);
    }

    [Fact]
    public void RejectsRunAndItemProblemCollectionsThatDisagree()
    {
        ExecutionManifest manifest = ManifestWithTwoMoves();
        ExecutionProblem failure = new(
            ExecutionProblemKind.MoveFailed,
            "The first operation failed.",
            operationOrdinal: 0);

        Assert.Throws<ArgumentException>(
            () => new ExecutionResult(
                manifest,
                StartedAt,
                FinishedAt,
                ExecutionOutcome.Stopped,
                [
                    new ExecutionItemResult(
                        0,
                        ExecutionItemOutcome.NotApplied,
                        [failure]),
                    new ExecutionItemResult(1, ExecutionItemOutcome.NotAttempted, []),
                ],
                []));
    }

    [Fact]
    public void RejectsACompletedRunThatStillReportsAFailure()
    {
        ExecutionManifest manifest = ManifestWithTwoMoves();
        ExecutionProblem journalFailure = new(
            ExecutionProblemKind.JournalFailure,
            "A completed run cannot retain a journal failure.");

        Assert.Throws<ArgumentException>(
            () => new ExecutionResult(
                manifest,
                StartedAt,
                FinishedAt,
                ExecutionOutcome.Completed,
                [
                    new ExecutionItemResult(0, ExecutionItemOutcome.Applied, []),
                    new ExecutionItemResult(1, ExecutionItemOutcome.Applied, []),
                ],
                [journalFailure]));
    }

    [Fact]
    public void RejectsAnInterruptedRunAfterEveryOperationWasApplied()
    {
        ExecutionManifest manifest = ManifestWithTwoMoves();

        Assert.Throws<ArgumentException>(
            () => new ExecutionResult(
                manifest,
                StartedAt,
                FinishedAt,
                ExecutionOutcome.Interrupted,
                [
                    new ExecutionItemResult(0, ExecutionItemOutcome.Applied, []),
                    new ExecutionItemResult(1, ExecutionItemOutcome.Applied, []),
                ],
                []));
    }

    [Fact]
    public void RepresentsAllAppliedMovesWithoutADurableCompletionAsRecoveryRequired()
    {
        ExecutionManifest manifest = ManifestWithTwoMoves();
        ExecutionProblem recovery = new(
            ExecutionProblemKind.RecoveryRequired,
            "Every move is durable, but the journal has no terminal completion record.");

        ExecutionResult result = new(
            manifest,
            StartedAt,
            FinishedAt,
            ExecutionOutcome.RecoveryRequired,
            [
                new ExecutionItemResult(0, ExecutionItemOutcome.Applied, []),
                new ExecutionItemResult(1, ExecutionItemOutcome.Applied, []),
            ],
            [recovery]);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.All(
            result.Items,
            item => Assert.Equal(ExecutionItemOutcome.Applied, item.Outcome));
        Assert.Equal(ExecutionProblemKind.RecoveryRequired, Assert.Single(result.Problems).Kind);
        Assert.False(result.WasReplayed);
    }

    [Fact]
    public void RejectsATerminalTimestampBeforeTheStartTimestamp()
    {
        ExecutionManifest manifest = ManifestWithTwoMoves();

        Assert.Throws<ArgumentException>(
            () => new ExecutionResult(
                manifest,
                StartedAt,
                StartedAt.AddTicks(-1),
                ExecutionOutcome.Completed,
                [
                    new ExecutionItemResult(0, ExecutionItemOutcome.Applied, []),
                    new ExecutionItemResult(1, ExecutionItemOutcome.Applied, []),
                ],
                []));
    }

    [Fact]
    public void SortsStructuredProblemsWithoutUsingCurrentCulture()
    {
        ExecutionManifest manifest = ManifestWithTwoMoves();
        ExecutionProblem later = new(
            ExecutionProblemKind.MoveFailed,
            "z problem",
            operationOrdinal: 1);
        ExecutionProblem earlier = new(
            ExecutionProblemKind.MoveFailed,
            "a problem",
            operationOrdinal: 1);

        ExecutionItemResult item = new(
            1,
            ExecutionItemOutcome.NotApplied,
            [later, earlier]);
        ExecutionResult result = new(
            manifest,
            StartedAt,
            FinishedAt,
            ExecutionOutcome.PartiallyCompleted,
            [new ExecutionItemResult(0, ExecutionItemOutcome.Applied, []), item],
            [later, earlier]);

        Assert.Equal(
            ["a problem", "z problem"],
            result.Problems.Select(problem => problem.Description));
        Assert.Equal(
            ["a problem", "z problem"],
            result.Items[1].Problems.Select(problem => problem.Description));
    }

    [Fact]
    public void NoWorkRequiresAnEligibleEmptyManifest()
    {
        ExecutionManifest empty = Assert.IsType<ExecutionManifest>(
            ExecutionManifestCompiler.Compile(ManifestTestPlan.Create([])).Manifest);

        ExecutionResult result = new(
            empty,
            StartedAt,
            FinishedAt,
            ExecutionOutcome.NoWork,
            [],
            []);

        Assert.Equal(ExecutionOutcome.NoWork, result.Outcome);
        Assert.Empty(result.Items);
    }

    private static ExecutionManifest ManifestWithTwoMoves() =>
        Assert.IsType<ExecutionManifest>(
            ExecutionManifestCompiler.Compile(
                ManifestTestPlan.Create(
                [
                    new ManifestTestPlan.TestOperation(
                        @"C:\Inbox\a.txt",
                        new MoveToDirectoryAction(@"C:\Sorted\Text"),
                        "text"),
                    new ManifestTestPlan.TestOperation(
                        @"C:\Inbox\b.zip",
                        new MoveToDirectoryAction(@"C:\Sorted\Archives"),
                        "archives"),
                ])).Manifest);
}
