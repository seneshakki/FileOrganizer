using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Application.Orchestration;
using FileOrganizer.Application.Recovery;
using FileOrganizer.Application.Tests.Execution;
using FileOrganizer.Application.Tests.Journaling;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;

namespace FileOrganizer.Application.Tests.Orchestration;

public sealed class OrganizationPlanExecutorTests
{
    private static readonly DateTimeOffset ExecutionTime =
        ManifestTestPlan.GeneratedAt.AddHours(1);

    [Fact]
    public async Task CompletesMovesInManifestOrderWithIntentBeforeEveryCommit()
    {
        OrganizationPlan plan = Plan(3);
        Harness context = new();

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(
            [ExecutionItemOutcome.Applied, ExecutionItemOutcome.Applied, ExecutionItemOutcome.Applied],
            result.Items.Select(item => item.Outcome));
        Assert.Equal([0, 1, 2], context.Moves.CommittedOrdinals);
        Assert.Equal(3, context.Moves.AcquisitionExecutionLeases.Count);
        Assert.All(
            context.Moves.AcquisitionExecutionLeases,
            lease => Assert.Same(context.Preflight.ExecutionLease, lease));
        Assert.Equal(3, context.Moves.AcquisitionManifests.Count);
        Assert.All(
            context.Moves.AcquisitionManifests,
            manifest => Assert.Same(result.Manifest, manifest));
        Assert.False(context.GlobalLease.IsOwned);
        Assert.Equal(
            [
                ExecutionJournalRecordKind.ExecutionStarted,
                ExecutionJournalRecordKind.MoveIntent,
                ExecutionJournalRecordKind.MoveApplied,
                ExecutionJournalRecordKind.MoveIntent,
                ExecutionJournalRecordKind.MoveApplied,
                ExecutionJournalRecordKind.MoveIntent,
                ExecutionJournalRecordKind.MoveApplied,
                ExecutionJournalRecordKind.ExecutionCompleted,
            ],
            context.Journal.Records(result.Fingerprint!).Select(record => record.Kind));
    }

    [Fact]
    public async Task GlobalLeaseSpansEveryDurabilityAndPhysicalBoundaryAndDisposesOnce()
    {
        OrganizationPlan plan = Plan(2);
        Harness context = new();
        int journalBoundariesWhileOwned = 0;
        context.Preflight.Handler = (manifest, _) =>
        {
            Assert.True(context.GlobalLease.IsOwned);
            return new ExecutionPreflightResult(
                manifest.Items.Select(item =>
                    ExecutionPreflightItemResult.Ready(item.Ordinal)));
        };
        context.Journal.OnBoundary = _ =>
        {
            Assert.True(context.GlobalLease.IsOwned);
            journalBoundariesWhileOwned++;
        };
        for (int ordinal = 0; ordinal < 2; ordinal++)
        {
            context.Moves[ordinal].OnCommitted = () => Assert.True(context.GlobalLease.IsOwned);
            context.Moves[ordinal].OnObserved = () => Assert.True(context.GlobalLease.IsOwned);
            context.Moves[ordinal].OnDisposed = () => Assert.True(context.GlobalLease.IsOwned);
        }

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(12, journalBoundariesWhileOwned);
        Assert.Equal([0, 1], context.Moves.DisposedOrdinals);
        Assert.Equal(1, context.GlobalLease.DisposeCount);
        Assert.False(context.GlobalLease.IsOwned);
    }

    [Fact]
    public async Task WholePlanPreflightReportsEveryFailureBeforeStartingAJournal()
    {
        OrganizationPlan plan = Plan(3);
        Harness context = new();
        context.Preflight.Handler = (manifest, _) => new ExecutionPreflightResult(
        [
            ExecutionPreflightItemResult.Blocked(
                0,
                Problem(
                    ExecutionProblemKind.SourceRevalidationFailed,
                    "The first source changed.",
                    0)),
            ExecutionPreflightItemResult.Ready(1),
            ExecutionPreflightItemResult.Conflict(
                2,
                Problem(
                    ExecutionProblemKind.DestinationConflict,
                    "The third target now exists.",
                    2)),
        ]);

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Stopped, result.Outcome);
        Assert.Equal(
            [ExecutionItemOutcome.Blocked, ExecutionItemOutcome.NotAttempted, ExecutionItemOutcome.Conflict],
            result.Items.Select(item => item.Outcome));
        Assert.Equal(
            [ExecutionProblemKind.SourceRevalidationFailed, ExecutionProblemKind.DestinationConflict],
            result.Problems.Select(problem => problem.Kind));
        Assert.Equal(1, context.GlobalLease.AcquisitionCount);
        Assert.Equal(1, context.Journal.ReadCount);
        Assert.Equal(0, context.Journal.BeginCount);
        Assert.Empty(context.Moves.AcquiredOrdinals);
    }

    [Fact]
    public async Task IdentityDeficientPreviewIsRejectedBeforeAnyExecutionPort()
    {
        OrganizationPlan plan = ManifestTestPlan.Create(
        [
            new ManifestTestPlan.TestOperation(
                @"C:\Inbox\preview.txt",
                new MoveToDirectoryAction(@"C:\Sorted\Text"),
                "move"),
        ],
            includeSourcePhysicalIdentities: false);
        Harness context = new();

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Rejected, result.Outcome);
        Assert.Null(result.Manifest);
        Assert.Equal(
            ExecutionManifestProblemKind.SourcePhysicalIdentityMissing,
            Assert.Single(result.ManifestProblems).Kind);
        Assert.Equal(0, context.GlobalLease.AcquisitionCount);
        Assert.Equal(0, context.Preflight.CallCount);
        Assert.Equal(0, context.Journal.BeginCount);
        Assert.Empty(context.Moves.AcquiredOrdinals);
        Assert.False(context.GlobalLease.IsOwned);
    }

    [Theory]
    [InlineData(OrganizationActionKind.CopyToDirectory)]
    [InlineData(OrganizationActionKind.RenameToLiteralFileName)]
    public async Task UnsupportedReadyActionIsRejectedBeforeAnyExecutionPort(
        OrganizationActionKind actionKind)
    {
        OrganizationAction action = actionKind switch
        {
            OrganizationActionKind.CopyToDirectory =>
                new CopyToDirectoryAction(@"C:\Sorted\Text"),
            OrganizationActionKind.RenameToLiteralFileName =>
                new RenameToLiteralFileNameAction("renamed.txt"),
            _ => throw new InvalidOperationException(),
        };
        OrganizationPlan plan = ManifestTestPlan.Create(
        [
            new ManifestTestPlan.TestOperation(
                @"C:\Inbox\source.txt",
                action,
                "unsupported"),
        ]);
        Harness context = new();

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains(
            result.ManifestProblems,
            problem => problem.Kind == ExecutionManifestProblemKind.UnsupportedReadyAction
                && problem.ActionKind == actionKind);
        Assert.Equal(0, context.GlobalLease.AcquisitionCount);
        Assert.Equal(0, context.Journal.ReadCount);
        Assert.Equal(0, context.Journal.BeginCount);
        Assert.Empty(context.Moves.AcquiredOrdinals);
    }

    [Fact]
    public async Task ImmediateRevalidationFailureStopsAfterTheAppliedPrefix()
    {
        OrganizationPlan plan = Plan(3);
        Harness context = new();
        context.Moves[1].AcquisitionReadiness = ExecutionReadiness.Blocked;
        context.Moves[1].AcquisitionProblem = Problem(
            ExecutionProblemKind.SourceRevalidationFailed,
            "The source metadata changed immediately before execution.",
            1);

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.PartiallyCompleted, result.Outcome);
        Assert.Equal(
            [ExecutionItemOutcome.Applied, ExecutionItemOutcome.Blocked, ExecutionItemOutcome.NotAttempted],
            result.Items.Select(item => item.Outcome));
        Assert.Equal([0], context.Moves.CommittedOrdinals);
        Assert.Equal(
            ExecutionJournalRecordKind.ExecutionStopped,
            context.Journal.Records(result.Fingerprint!)[^1].Kind);
    }

    [Fact]
    public async Task SourceIdentityMismatchStopsBeforeMoveIntentOrCommit()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Moves[0].Identity = TestPhysicalIdentities.File("different-source");

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Stopped, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.Blocked, Assert.Single(result.Items).Outcome);
        Assert.Equal(
            ExecutionProblemKind.SourceRevalidationFailed,
            Assert.Single(result.Problems).Kind);
        Assert.Empty(context.Moves.CommittedOrdinals);
        Assert.Equal(
            [
                ExecutionJournalRecordKind.ExecutionStarted,
                ExecutionJournalRecordKind.ExecutionStopped,
            ],
            context.Journal.Records(result.Fingerprint!).Select(record => record.Kind));
    }

    [Fact]
    public async Task DestinationDirectoryIdentityMismatchStopsBeforeMoveIntentOrCommit()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Moves[0].DestinationDirectoryIdentityOverride =
            TestPhysicalIdentities.Directory("different-destination");

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Stopped, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.Blocked, Assert.Single(result.Items).Outcome);
        Assert.Equal(
            ExecutionProblemKind.DestinationRevalidationFailed,
            Assert.Single(result.Problems).Kind);
        Assert.Empty(context.Moves.CommittedOrdinals);
        Assert.Equal(
            [
                ExecutionJournalRecordKind.ExecutionStarted,
                ExecutionJournalRecordKind.ExecutionStopped,
            ],
            context.Journal.Records(result.Fingerprint!).Select(record => record.Kind));
    }

    [Fact]
    public async Task MoveIntentRetainsTheManifestSourceIdentityAfterLeaseValidation()
    {
        OrganizationPlan plan = Plan(1);
        ExecutionManifest manifest = Compile(plan);
        Harness context = new();

        ExecutionResult result = await context.ExecuteAsync(plan);

        ExecutionJournalRecord intent = Assert.Single(
            context.Journal.Records(result.Fingerprint!),
            record => record.Kind == ExecutionJournalRecordKind.MoveIntent);
        Assert.Same(
            Assert.Single(manifest.Items).SourcePrecondition.PhysicalIdentity,
            intent.ExpectedEntryIdentity);
    }

    [Fact]
    public async Task DefiniteNotAppliedMoveStopsWithoutTryingLaterItems()
    {
        OrganizationPlan plan = Plan(2);
        Harness context = new();
        context.Moves[0].PostCommitObservation =
            ScriptedMoveStep.SourceOnly(context.Moves[0].Identity);
        context.Moves[0].AttemptResult = MoveAttemptResult.Failure(
            Problem(ExecutionProblemKind.MoveFailed, "The scripted move failed.", 0));

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Stopped, result.Outcome);
        Assert.Equal(
            [ExecutionItemOutcome.NotApplied, ExecutionItemOutcome.NotAttempted],
            result.Items.Select(item => item.Outcome));
        Assert.Equal([0], context.Moves.CommittedOrdinals);
        Assert.Equal(
            ExecutionJournalStatus.Resumable,
            ExecutionJournalStateMachine.Replay(
                new ExecutionJournalSnapshot(
                    context.Journal.Records(result.Fingerprint!)[0].RunId,
                    result.Manifest!,
                    context.Journal.Records(result.Fingerprint!))).Status);
    }

    [Theory]
    [InlineData(ExecutionProblemKind.SourceRevalidationFailed, JournalRecordReasonKind.SourceRevalidationFailed)]
    [InlineData(ExecutionProblemKind.DestinationRevalidationFailed, JournalRecordReasonKind.DestinationRevalidationFailed)]
    public async Task FinalRevalidationBlockRemainsBlockedAfterIntent(
        ExecutionProblemKind problemKind,
        JournalRecordReasonKind journalReason)
    {
        OrganizationPlan plan = Plan(2);
        Harness context = new();
        context.Moves[0].PostCommitObservation =
            ScriptedMoveStep.SourceOnly(context.Moves[0].Identity);
        context.Moves[0].AttemptResult = MoveAttemptResult.Blocked(
            Problem(problemKind, "Final revalidation prevented mutation.", 0));

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Stopped, result.Outcome);
        Assert.Equal(
            [ExecutionItemOutcome.Blocked, ExecutionItemOutcome.NotAttempted],
            result.Items.Select(item => item.Outcome));
        Assert.Equal(problemKind, Assert.Single(result.Problems).Kind);
        ExecutionJournalRecord record = context.Journal.Records(result.Fingerprint!)[^1];
        Assert.Equal(ExecutionJournalRecordKind.MoveNotApplied, record.Kind);
        Assert.Equal(journalReason, record.Reason);
        Assert.Equal(
            ExecutionJournalStatus.Blocked,
            ExecutionJournalStateMachine.Replay(
                new ExecutionJournalSnapshot(
                    context.Journal.Records(result.Fingerprint!)[0].RunId,
                    result.Manifest!,
                    context.Journal.Records(result.Fingerprint!))).Status);
    }

    [Fact]
    public async Task NativeTargetRaceConflictRemainsConflictWhenObservationProvesUnmoved()
    {
        OrganizationPlan plan = Plan(2);
        Harness context = new();
        context.Moves[0].PostCommitObservation =
            ScriptedMoveStep.SourceOnly(context.Moves[0].Identity);
        context.Moves[0].AttemptResult = MoveAttemptResult.Conflict(
            Problem(
                ExecutionProblemKind.DestinationConflict,
                "The target appeared at the native no-overwrite boundary.",
                0));

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Stopped, result.Outcome);
        Assert.Equal(
            [ExecutionItemOutcome.Conflict, ExecutionItemOutcome.NotAttempted],
            result.Items.Select(item => item.Outcome));
        Assert.Equal(
            ExecutionProblemKind.DestinationConflict,
            Assert.Single(result.Problems).Kind);
        ExecutionJournalRecord record = context.Journal.Records(result.Fingerprint!)[^1];
        Assert.Equal(ExecutionJournalRecordKind.MoveConflict, record.Kind);
        Assert.Equal(JournalRecordReasonKind.DestinationConflict, record.Reason);
    }

    [Fact]
    public async Task ReconciledTargetIdentityOverridesAReportedNativeFailureAsApplied()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Moves[0].AttemptResult = MoveAttemptResult.Failure(
            Problem(
                ExecutionProblemKind.MoveFailed,
                "The native call reported failure before reconciliation.",
                0));

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.Applied, Assert.Single(result.Items).Outcome);
        Assert.Empty(result.Problems);
        Assert.Equal(
            [
                ExecutionJournalRecordKind.ExecutionStarted,
                ExecutionJournalRecordKind.MoveIntent,
                ExecutionJournalRecordKind.MoveApplied,
                ExecutionJournalRecordKind.ExecutionCompleted,
            ],
            context.Journal.Records(result.Fingerprint!).Select(record => record.Kind));
    }

    [Fact]
    public async Task FailedPostMoveInspectionOverridesReportedSuccessAsAmbiguous()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Moves[0].PostCommitObservation = new MoveObservation(
            RecoveryEntryObservation.InspectionFailed(
                RecoveryInspectionFailureKind.IoFailure),
            RecoveryEntryObservation.Missing());

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.Ambiguous, Assert.Single(result.Items).Outcome);
        Assert.Equal(
            ExecutionProblemKind.MoveOutcomeAmbiguous,
            Assert.Single(result.Problems).Kind);
        ExecutionJournalRecord record = context.Journal.Records(result.Fingerprint!)[^1];
        Assert.Equal(ExecutionJournalRecordKind.MoveAmbiguous, record.Kind);
        Assert.Equal(JournalRecordReasonKind.RecoveryInspectionFailed, record.Reason);
    }

    [Fact]
    public async Task AppliedThenConflictStopsBeforeTheRemainingMove()
    {
        OrganizationPlan plan = Plan(3);
        Harness context = new();
        context.Moves[1].PostCommitObservation =
            ScriptedMoveStep.SourceOnly(context.Moves[1].Identity);
        context.Moves[1].AttemptResult = MoveAttemptResult.Conflict(
            Problem(
                ExecutionProblemKind.DestinationConflict,
                "The second target appeared at commit.",
                1));

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.PartiallyCompleted, result.Outcome);
        Assert.Equal(
            [ExecutionItemOutcome.Applied, ExecutionItemOutcome.Conflict, ExecutionItemOutcome.NotAttempted],
            result.Items.Select(item => item.Outcome));
        Assert.Equal([0, 1], context.Moves.CommittedOrdinals);
        Assert.Equal(
            ExecutionJournalRecordKind.MoveConflict,
            context.Journal.Records(result.Fingerprint!)[^1].Kind);
    }

    [Fact]
    public async Task AppliedThenAmbiguousStopsBeforeTheRemainingMove()
    {
        OrganizationPlan plan = Plan(3);
        Harness context = new();
        context.Moves[1].PostCommitObservation = new MoveObservation(
            RecoveryEntryObservation.InspectionFailed(
                RecoveryInspectionFailureKind.IoFailure),
            RecoveryEntryObservation.Missing());

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(
            [ExecutionItemOutcome.Applied, ExecutionItemOutcome.Ambiguous, ExecutionItemOutcome.NotAttempted],
            result.Items.Select(item => item.Outcome));
        Assert.Equal([0, 1], context.Moves.CommittedOrdinals);
        Assert.Equal(
            ExecutionJournalRecordKind.MoveAmbiguous,
            context.Journal.Records(result.Fingerprint!)[^1].Kind);
    }

    [Fact]
    public async Task CancellationBeforeLeaseAcquisitionTouchesNoExecutionPort()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => context.Executor.ExecuteAsync(plan, cancellation.Token).AsTask());

        Assert.Equal(0, context.GlobalLease.AcquisitionCount);
        Assert.Equal(0, context.GlobalLease.DisposeCount);
        Assert.Equal(0, context.Journal.ReadCount);
        Assert.Equal(0, context.Preflight.CallCount);
        Assert.Empty(context.Moves.AcquiredOrdinals);
    }

    [Fact]
    public async Task CancellationBeforeJournalCreationUsesNormalCancellationSemantics()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        context.Preflight.Handler = (_, _) =>
        {
            cancellation.Cancel();
            return new ExecutionPreflightResult(
            [
                ExecutionPreflightItemResult.Blocked(
                    0,
                    Problem(
                        ExecutionProblemKind.SourceRevalidationFailed,
                        "The cooperative preflight returned after cancellation.",
                        0)),
            ]);
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => context.Executor.ExecuteAsync(plan, cancellation.Token).AsTask());

        Assert.Equal(0, context.Journal.BeginCount);
        Assert.Empty(context.Moves.CommittedOrdinals);
        Assert.Equal(1, context.GlobalLease.AcquisitionCount);
        Assert.Equal(1, context.GlobalLease.DisposeCount);
        Assert.False(context.GlobalLease.IsOwned);
    }

    [Fact]
    public async Task CancellationAfterJournalStartIsDurablyInterruptedBeforeAnyIntent()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary.Boundary == JournalFaultBoundary.AfterBegin)
            {
                cancellation.Cancel();
            }
        };

        ExecutionResult result = await context.Executor.ExecuteAsync(plan, cancellation.Token);

        Assert.Equal(ExecutionOutcome.Interrupted, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.NotAttempted, Assert.Single(result.Items).Outcome);
        Assert.Empty(context.Moves.AcquiredOrdinals);
        Assert.Equal(
            [ExecutionJournalRecordKind.ExecutionStarted, ExecutionJournalRecordKind.ExecutionInterrupted],
            context.Journal.Records(result.Fingerprint!).Select(record => record.Kind));
        Assert.Equal(1, context.GlobalLease.DisposeCount);
        Assert.False(context.GlobalLease.IsOwned);
    }

    [Fact]
    public async Task CancellationAfterIntentIsDurablyNotAppliedBeforeNativeInvocation()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary is
                {
                    Boundary: JournalFaultBoundary.AfterPublish,
                    Record.Kind: ExecutionJournalRecordKind.MoveIntent,
                })
            {
                cancellation.Cancel();
            }
        };

        ExecutionResult result = await context.Executor.ExecuteAsync(plan, cancellation.Token);

        Assert.Equal(ExecutionOutcome.Interrupted, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.NotApplied, Assert.Single(result.Items).Outcome);
        Assert.Equal(
            ExecutionProblemKind.CancellationRequested,
            Assert.Single(result.Problems).Kind);
        Assert.Empty(context.Moves.CommittedOrdinals);
        Assert.Equal(
            [
                ExecutionJournalRecordKind.ExecutionStarted,
                ExecutionJournalRecordKind.MoveIntent,
                ExecutionJournalRecordKind.MoveNotApplied,
            ],
            context.Journal.Records(result.Fingerprint!).Select(record => record.Kind));
        Assert.Equal(
            JournalRecordReasonKind.CancellationRequested,
            context.Journal.Records(result.Fingerprint!)[^1].Reason);

        ExecutionResult replay = await context.ExecuteAsync(plan);
        Assert.Equal(ExecutionOutcome.RecoveryRequired, replay.Outcome);
        Assert.Equal(ExecutionItemOutcome.NotApplied, Assert.Single(replay.Items).Outcome);
        Assert.Equal(
            ExecutionProblemKind.CancellationRequested,
            Assert.Single(replay.Items[0].Problems).Kind);
        Assert.Empty(context.Moves.CommittedOrdinals);
    }

    [Fact]
    public async Task CancellationOutcomePublicationFailureRequiresRecoveryWithoutNativeInvocation()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary is
                {
                    Boundary: JournalFaultBoundary.AfterPublish,
                    Record.Kind: ExecutionJournalRecordKind.MoveIntent,
                })
            {
                cancellation.Cancel();
            }
        };
        context.Journal.PublishOverride = record =>
            record.Kind == ExecutionJournalRecordKind.MoveNotApplied
                ? JournalPublishResult.PersistenceFailure()
                : null;

        ExecutionResult result = await context.Executor.ExecuteAsync(plan, cancellation.Token);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.NotApplied, Assert.Single(result.Items).Outcome);
        Assert.Equal(
            [
                ExecutionProblemKind.JournalFailure,
                ExecutionProblemKind.RecoveryRequired,
                ExecutionProblemKind.CancellationRequested,
            ],
            result.Problems.Select(problem => problem.Kind));
        Assert.Empty(context.Moves.CommittedOrdinals);
        Assert.Equal(
            [ExecutionJournalRecordKind.ExecutionStarted, ExecutionJournalRecordKind.MoveIntent],
            context.Journal.Records(result.Fingerprint!).Select(record => record.Kind));
    }

    [Fact]
    public async Task CancellationDuringCommitWaitsForObservationAndDurableOutcome()
    {
        OrganizationPlan plan = Plan(2);
        Harness context = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        context.Moves[0].OnCommitted = cancellation.Cancel;

        ExecutionResult result = await context.Executor.ExecuteAsync(plan, cancellation.Token);

        Assert.Equal(ExecutionOutcome.Interrupted, result.Outcome);
        Assert.Equal(
            [ExecutionItemOutcome.Applied, ExecutionItemOutcome.NotAttempted],
            result.Items.Select(item => item.Outcome));
        Assert.Equal([0], context.Moves.CommittedOrdinals);
        Assert.Equal([0], context.Moves.ObservedOrdinals);
        Assert.Equal(
            [
                ExecutionJournalRecordKind.ExecutionStarted,
                ExecutionJournalRecordKind.MoveIntent,
                ExecutionJournalRecordKind.MoveApplied,
                ExecutionJournalRecordKind.ExecutionInterrupted,
            ],
            context.Journal.Records(result.Fingerprint!).Select(record => record.Kind));
    }

    [Fact]
    public async Task CancellationDuringFinalCommitRecordsAppliedThenInterrupts()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        context.Moves[0].OnCommitted = cancellation.Cancel;

        ExecutionResult result = await context.Executor.ExecuteAsync(plan, cancellation.Token);

        Assert.Equal(ExecutionOutcome.Interrupted, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.Applied, Assert.Single(result.Items).Outcome);
        Assert.Equal([0], context.Moves.ObservedOrdinals);
        Assert.Equal(
            ExecutionJournalRecordKind.ExecutionInterrupted,
            context.Journal.Records(result.Fingerprint!)[^1].Kind);
        Assert.DoesNotContain(
            context.Journal.Records(result.Fingerprint!),
            record => record.Kind == ExecutionJournalRecordKind.ExecutionCompleted);
    }

    [Fact]
    public async Task CancellationAfterDurableFinalOutcomePreventsTerminalCompletion()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary is
                {
                    Boundary: JournalFaultBoundary.AfterPublish,
                    Record.Kind: ExecutionJournalRecordKind.MoveApplied,
                })
            {
                cancellation.Cancel();
            }
        };

        ExecutionResult result = await context.Executor.ExecuteAsync(plan, cancellation.Token);

        Assert.Equal(ExecutionOutcome.Interrupted, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.Applied, Assert.Single(result.Items).Outcome);
        Assert.Equal(
            [
                ExecutionJournalRecordKind.ExecutionStarted,
                ExecutionJournalRecordKind.MoveIntent,
                ExecutionJournalRecordKind.MoveApplied,
                ExecutionJournalRecordKind.ExecutionInterrupted,
            ],
            context.Journal.Records(result.Fingerprint!).Select(record => record.Kind));
        Assert.Single(context.Moves.CommittedOrdinals);
        Assert.Equal([0], context.Moves.DisposedOrdinals);
        Assert.Equal(1, context.GlobalLease.DisposeCount);
    }

    [Theory]
    [InlineData(
        MoveAttemptOutcome.Failed,
        ExecutionItemOutcome.NotApplied,
        ExecutionProblemKind.MoveFailed,
        ExecutionJournalRecordKind.MoveNotApplied)]
    [InlineData(
        MoveAttemptOutcome.Blocked,
        ExecutionItemOutcome.Blocked,
        ExecutionProblemKind.SourceRevalidationFailed,
        ExecutionJournalRecordKind.MoveNotApplied)]
    [InlineData(
        MoveAttemptOutcome.Conflict,
        ExecutionItemOutcome.Conflict,
        ExecutionProblemKind.DestinationConflict,
        ExecutionJournalRecordKind.MoveConflict)]
    public async Task CancellationAroundDefinitivePhysicalOutcomePreservesItAndInterrupts(
        MoveAttemptOutcome attemptOutcome,
        ExecutionItemOutcome expectedItemOutcome,
        ExecutionProblemKind expectedProblemKind,
        ExecutionJournalRecordKind expectedRecordKind)
    {
        OrganizationPlan plan = Plan(3);
        Harness context = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        ExecutionProblem operationProblem = Problem(
            expectedProblemKind,
            "The physical operation stopped definitively.",
            0);
        context.Moves[0].PostCommitObservation =
            ScriptedMoveStep.SourceOnly(context.Moves[0].Identity);
        context.Moves[0].AttemptResult = attemptOutcome switch
        {
            MoveAttemptOutcome.Failed => MoveAttemptResult.Failure(operationProblem),
            MoveAttemptOutcome.Blocked => MoveAttemptResult.Blocked(operationProblem),
            MoveAttemptOutcome.Conflict => MoveAttemptResult.Conflict(operationProblem),
            _ => throw new InvalidOperationException(),
        };
        context.Moves[0].OnCommitted = cancellation.Cancel;

        ExecutionResult result = await context.Executor.ExecuteAsync(plan, cancellation.Token);

        Assert.Equal(ExecutionOutcome.Interrupted, result.Outcome);
        Assert.Equal(
            [expectedItemOutcome, ExecutionItemOutcome.NotAttempted, ExecutionItemOutcome.NotAttempted],
            result.Items.Select(item => item.Outcome));
        Assert.Equal(expectedProblemKind, Assert.Single(result.Items[0].Problems).Kind);
        Assert.Contains(
            result.Problems,
            problem => problem.Kind == ExecutionProblemKind.CancellationRequested);
        Assert.Equal([0], context.Moves.CommittedOrdinals);
        Assert.Equal([0], context.Moves.ObservedOrdinals);
        Assert.Equal(
            expectedRecordKind,
            context.Journal.Records(result.Fingerprint!)[^1].Kind);
        Assert.DoesNotContain(
            context.Journal.Records(result.Fingerprint!),
            record => record.Kind == ExecutionJournalRecordKind.ExecutionInterrupted);
    }

    [Fact]
    public async Task CancellationAroundAmbiguousPhysicalOutcomePreservesRecoveryRequired()
    {
        OrganizationPlan plan = Plan(2);
        Harness context = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        context.Moves[0].PostCommitObservation = new MoveObservation(
            RecoveryEntryObservation.InspectionFailed(
                RecoveryInspectionFailureKind.IoFailure),
            RecoveryEntryObservation.Missing());
        context.Moves[0].OnCommitted = cancellation.Cancel;

        ExecutionResult result = await context.Executor.ExecuteAsync(plan, cancellation.Token);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(
            [ExecutionItemOutcome.Ambiguous, ExecutionItemOutcome.NotAttempted],
            result.Items.Select(item => item.Outcome));
        Assert.Equal(
            ExecutionProblemKind.MoveOutcomeAmbiguous,
            Assert.Single(result.Items[0].Problems).Kind);
        Assert.Contains(
            result.Problems,
            problem => problem.Kind == ExecutionProblemKind.CancellationRequested);
        Assert.Equal([0], context.Moves.CommittedOrdinals);
        Assert.Equal([0], context.Moves.ObservedOrdinals);
        Assert.Equal(
            ExecutionJournalRecordKind.MoveAmbiguous,
            context.Journal.Records(result.Fingerprint!)[^1].Kind);
        Assert.DoesNotContain(
            context.Journal.Records(result.Fingerprint!),
            record => record.Kind == ExecutionJournalRecordKind.ExecutionInterrupted);
    }

    [Fact]
    public async Task CompletedJournalIsReplayedWithoutPreflightOrMoveCalls()
    {
        OrganizationPlan plan = Plan(2);
        Harness context = new();
        ExecutionResult first = await context.ExecuteAsync(plan);
        int preflightCalls = context.Preflight.CallCount;
        int moveCalls = context.Moves.CommittedOrdinals.Count;

        ExecutionResult replay = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Completed, first.Outcome);
        Assert.Equal(ExecutionOutcome.Completed, replay.Outcome);
        Assert.True(replay.WasReplayed);
        Assert.Equal(preflightCalls, context.Preflight.CallCount);
        Assert.Equal(moveCalls, context.Moves.CommittedOrdinals.Count);
        Assert.Equal(1, context.Journal.BeginCount);
    }

    [Fact]
    public async Task CorruptDurableReadRequiresRecoveryBeforePreflightOrExecution()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Journal.ReplayReadOverride = _ => new ExecutionJournalReadResult(
            ExecutionJournalReadStatus.Invalid,
            problems:
            [
                new ExecutionJournalReadProblem(
                    ExecutionJournalReadProblemKind.PayloadIntegrityViolation,
                    recordSequence: 1),
            ]);

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.All(
            result.Items,
            item => Assert.Equal(ExecutionItemOutcome.NotAttempted, item.Outcome));
        Assert.Equal(
            [ExecutionProblemKind.InvalidJournal, ExecutionProblemKind.RecoveryRequired],
            result.Problems.Select(static problem => problem.Kind));
        Assert.Equal(0, context.Preflight.CallCount);
        Assert.Equal(0, context.Journal.BeginCount);
        Assert.Empty(context.Moves.AcquiredOrdinals);
    }

    [Fact]
    public async Task CorruptDurableReadRequiresManualRecoveryWithoutObservationOrPublication()
    {
        OrganizationPlan plan = Plan(1);
        PlanFingerprint fingerprint = Compile(plan).Fingerprint;
        Harness context = new();
        context.Journal.ReplayReadOverride = _ => new ExecutionJournalReadResult(
            ExecutionJournalReadStatus.Invalid,
            problems:
            [
                new ExecutionJournalReadProblem(
                    ExecutionJournalReadProblemKind.ManifestMalformed),
            ]);

        ExecutionRecoveryResult result = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionRecoveryOutcome.InvalidJournal, result.Outcome);
        Assert.Equal(
            ExecutionJournalReadProblemKind.ManifestMalformed,
            Assert.Single(result.JournalProblems).Kind);
        Assert.Empty(context.Moves.RecoveryObservedOrdinals);
        Assert.Equal(0, context.Journal.PublishCount);
    }

    [Fact]
    public async Task JournalCorruptionDiscoveredAtAtomicBeginFailsStopBeforeAnyMove()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Journal.BeginOverride = (manifest, _) =>
            JournalBeginResult.InvalidExistingJournal(
                manifest.Fingerprint,
                [
                    new ExecutionJournalReadProblem(
                        ExecutionJournalReadProblemKind.HashChainViolation,
                        recordSequence: 2),
                ]);

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.NotAttempted, Assert.Single(result.Items).Outcome);
        Assert.Contains(
            result.Problems,
            problem => problem.Kind == ExecutionProblemKind.InvalidJournal);
        Assert.Equal(1, context.Preflight.CallCount);
        Assert.Equal(1, context.Journal.BeginCount);
        Assert.Empty(context.Moves.AcquiredOrdinals);
    }

    [Fact]
    public async Task PendingIntentRequiresRecoveryAndIsNeverRetriedByExecute()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Moves[0].FaultBeforeStateChange =
            new SimulatedCrashException("Crash after intent.");

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => context.ExecuteAsync(plan).AsTask());
        int commitCalls = context.Moves.CommittedOrdinals.Count;

        ExecutionResult retry = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, retry.Outcome);
        Assert.Equal(ExecutionItemOutcome.Ambiguous, Assert.Single(retry.Items).Outcome);
        Assert.Equal(commitCalls, context.Moves.CommittedOrdinals.Count);
    }

    [Fact]
    public async Task CrashImmediatelyAfterDurableIntentNeverCommitsPendingOperation()
    {
        OrganizationPlan plan = Plan(4);
        Harness context = new();
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary is
                {
                    Boundary: JournalFaultBoundary.AfterPublish,
                    Record.Kind: ExecutionJournalRecordKind.MoveIntent,
                    Record.OperationOrdinal: 2,
                })
            {
                throw new SimulatedCrashException("Crash immediately after durable intent.");
            }
        };

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => context.ExecuteAsync(plan).AsTask());

        PlanFingerprint fingerprint = Compile(plan).Fingerprint;
        Assert.Equal(
            [
                ExecutionJournalRecordKind.ExecutionStarted,
                ExecutionJournalRecordKind.MoveIntent,
                ExecutionJournalRecordKind.MoveApplied,
                ExecutionJournalRecordKind.MoveIntent,
                ExecutionJournalRecordKind.MoveApplied,
                ExecutionJournalRecordKind.MoveIntent,
            ],
            context.Journal.Records(fingerprint).Select(record => record.Kind));
        Assert.Equal([0, 1], context.Moves.CommittedOrdinals);
        Assert.Equal([0, 1, 2], context.Moves.DisposedOrdinals);
        Assert.Equal(1, context.GlobalLease.DisposeCount);

        context.Journal.OnBoundary = null;
        ExecutionResult retry = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, retry.Outcome);
        Assert.Equal(
            [
                ExecutionItemOutcome.Applied,
                ExecutionItemOutcome.Applied,
                ExecutionItemOutcome.Ambiguous,
                ExecutionItemOutcome.NotAttempted,
            ],
            retry.Items.Select(item => item.Outcome));
        Assert.Equal([0, 1], context.Moves.CommittedOrdinals);
        Assert.Empty(context.Moves.RecoveryObservedOrdinals);
        Assert.Equal(2, context.GlobalLease.DisposeCount);
    }

    [Fact]
    public async Task DurableAmbiguousOutcomeNeverAutoResumesExecution()
    {
        OrganizationPlan plan = Plan(1);
        ExecutionManifest manifest = Compile(plan);
        Harness context = new();
        context.Journal.Seed(
            manifest,
            [
                ExecutionJournalRecord.ExecutionStarted(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    0,
                    ExecutionTime),
                ExecutionJournalRecord.MoveIntent(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    1,
                    ExecutionTime.AddSeconds(1),
                    0,
                    manifest.Items[0].SourcePrecondition.PhysicalIdentity!),
                ExecutionJournalRecord.MoveAmbiguous(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    2,
                    ExecutionTime.AddSeconds(2),
                    0,
                    JournalRecordReasonKind.MoveOutcomeUnknown),
            ]);

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.Ambiguous, Assert.Single(result.Items).Outcome);
        Assert.Equal(0, context.Preflight.CallCount);
        Assert.Equal(0, context.Journal.BeginCount);
        Assert.Empty(context.Moves.AcquiredOrdinals);
        Assert.Empty(context.Moves.CommittedOrdinals);
    }

    [Fact]
    public async Task GenesisPersistenceFailureStopsBeforeAnyPhysicalMutation()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Journal.BeginOverride = (_, _) => JournalBeginResult.PersistenceFailure();

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Stopped, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.NotAttempted, Assert.Single(result.Items).Outcome);
        Assert.Equal(ExecutionProblemKind.JournalFailure, Assert.Single(result.Problems).Kind);
        Assert.Empty(context.Moves.AcquiredOrdinals);
        Assert.Empty(context.Moves.CommittedOrdinals);
        Assert.Empty(context.Journal.Records(result.Fingerprint!));
        Assert.Equal(1, context.GlobalLease.DisposeCount);
        Assert.False(context.GlobalLease.IsOwned);
    }

    [Fact]
    public async Task CrashBeforeGenesisPublicationLeavesNoJournalOrPhysicalAttempt()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary.Boundary == JournalFaultBoundary.BeforeBegin)
            {
                throw new SimulatedCrashException("Crash before journal genesis publication.");
            }
        };

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => context.ExecuteAsync(plan).AsTask());

        Assert.Empty(context.Journal.Records(Compile(plan).Fingerprint));
        Assert.Empty(context.Moves.AcquiredOrdinals);
        Assert.Empty(context.Moves.CommittedOrdinals);
        Assert.Equal(1, context.GlobalLease.DisposeCount);
        Assert.False(context.GlobalLease.IsOwned);
    }

    [Fact]
    public async Task IntentPersistenceFailureRequiresRecoveryAndNeverInvokesMover()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Journal.PublishOverride = record =>
            record.Kind == ExecutionJournalRecordKind.MoveIntent
                ? JournalPublishResult.PersistenceFailure()
                : null;

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.NotAttempted, Assert.Single(result.Items).Outcome);
        Assert.Equal(
            [ExecutionProblemKind.JournalFailure, ExecutionProblemKind.RecoveryRequired],
            result.Problems.Select(problem => problem.Kind));
        Assert.Empty(context.Moves.CommittedOrdinals);
        Assert.Equal(
            [ExecutionJournalRecordKind.ExecutionStarted],
            context.Journal.Records(result.Fingerprint!).Select(record => record.Kind));
        Assert.Equal([0], context.Moves.DisposedOrdinals);
        Assert.Equal(1, context.GlobalLease.DisposeCount);
        Assert.False(context.GlobalLease.IsOwned);
    }

    [Fact]
    public async Task AppliedMoveResultPersistenceFailureStopsAndCannotRetryMutation()
    {
        OrganizationPlan plan = Plan(2);
        Harness context = new();
        context.Journal.PublishOverride = record =>
            record.Kind == ExecutionJournalRecordKind.MoveApplied
                ? JournalPublishResult.PersistenceFailure()
                : null;

        ExecutionResult result = await context.ExecuteAsync(plan);
        int commitsAfterFailure = context.Moves.CommittedOrdinals.Count;
        ExecutionResult retry = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(
            [ExecutionItemOutcome.Applied, ExecutionItemOutcome.NotAttempted],
            result.Items.Select(item => item.Outcome));
        Assert.Equal(
            [ExecutionProblemKind.JournalFailure, ExecutionProblemKind.RecoveryRequired],
            result.Problems.Select(problem => problem.Kind));
        Assert.Equal(1, commitsAfterFailure);
        Assert.Equal(commitsAfterFailure, context.Moves.CommittedOrdinals.Count);
        Assert.Equal(ExecutionOutcome.RecoveryRequired, retry.Outcome);
        Assert.Equal(
            [ExecutionJournalRecordKind.ExecutionStarted, ExecutionJournalRecordKind.MoveIntent],
            context.Journal.Records(result.Fingerprint!).Select(record => record.Kind));
    }

    [Theory]
    [InlineData(
        MoveAttemptOutcome.Blocked,
        ExecutionItemOutcome.Blocked,
        ExecutionProblemKind.SourceRevalidationFailed,
        ExecutionJournalRecordKind.MoveNotApplied)]
    [InlineData(
        MoveAttemptOutcome.Conflict,
        ExecutionItemOutcome.Conflict,
        ExecutionProblemKind.DestinationConflict,
        ExecutionJournalRecordKind.MoveConflict)]
    public async Task ResultPersistenceFailurePreservesExactDefinitivePhysicalOutcome(
        MoveAttemptOutcome attemptOutcome,
        ExecutionItemOutcome expectedItemOutcome,
        ExecutionProblemKind expectedProblemKind,
        ExecutionJournalRecordKind failedRecordKind)
    {
        OrganizationPlan plan = Plan(2);
        Harness context = new();
        context.Moves[0].PostCommitObservation =
            ScriptedMoveStep.SourceOnly(context.Moves[0].Identity);
        ExecutionProblem operationProblem = Problem(
            expectedProblemKind,
            "The physical operation stopped definitively.",
            0);
        context.Moves[0].AttemptResult = attemptOutcome switch
        {
            MoveAttemptOutcome.Blocked => MoveAttemptResult.Blocked(operationProblem),
            MoveAttemptOutcome.Conflict => MoveAttemptResult.Conflict(operationProblem),
            _ => throw new InvalidOperationException(),
        };
        context.Journal.PublishOverride = record => record.Kind == failedRecordKind
            ? JournalPublishResult.PersistenceFailure()
            : null;

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(
            [expectedItemOutcome, ExecutionItemOutcome.NotAttempted],
            result.Items.Select(item => item.Outcome));
        Assert.Equal(expectedProblemKind, Assert.Single(result.Items[0].Problems).Kind);
        Assert.Contains(
            result.Problems,
            problem => problem.Kind == ExecutionProblemKind.JournalFailure);
        Assert.Contains(
            result.Problems,
            problem => problem.Kind == ExecutionProblemKind.RecoveryRequired);
        Assert.Equal([0], context.Moves.CommittedOrdinals);
        Assert.Equal(
            [ExecutionJournalRecordKind.ExecutionStarted, ExecutionJournalRecordKind.MoveIntent],
            context.Journal.Records(result.Fingerprint!).Select(record => record.Kind));
    }

    [Fact]
    public async Task TerminalPersistenceFailureRetainsAppliedItemsWithoutRepeatingMoves()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Journal.PublishOverride = record =>
            record.Kind == ExecutionJournalRecordKind.ExecutionCompleted
                ? JournalPublishResult.PersistenceFailure()
                : null;

        ExecutionResult result = await context.ExecuteAsync(plan);
        int commitsAfterFailure = context.Moves.CommittedOrdinals.Count;
        ExecutionResult retry = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.Applied, Assert.Single(result.Items).Outcome);
        Assert.Equal(
            [ExecutionProblemKind.JournalFailure, ExecutionProblemKind.RecoveryRequired],
            result.Problems.Select(problem => problem.Kind));
        Assert.Equal(1, commitsAfterFailure);
        Assert.Equal(commitsAfterFailure, context.Moves.CommittedOrdinals.Count);
        Assert.Equal(ExecutionOutcome.RecoveryRequired, retry.Outcome);
        Assert.Equal(
            ExecutionJournalRecordKind.MoveApplied,
            context.Journal.Records(result.Fingerprint!)[^1].Kind);
    }

    [Fact]
    public async Task MoveAppliedWithoutCompletionRequiresRecoverAndIsNotClaimedComplete()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary.Boundary == JournalFaultBoundary.BeforePublish
                && boundary.Record.Kind == ExecutionJournalRecordKind.ExecutionCompleted)
            {
                throw new SimulatedCrashException("Crash before completion publication.");
            }
        };

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => context.ExecuteAsync(plan).AsTask());
        context.Journal.OnBoundary = null;

        ExecutionResult retry = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, retry.Outcome);
        Assert.Equal(ExecutionItemOutcome.Applied, Assert.Single(retry.Items).Outcome);

        ExecutionRecoveryResult recovery = await context.RecoverAsync(
            retry.Fingerprint!);
        Assert.Equal(ExecutionRecoveryOutcome.Completed, recovery.Outcome);

        ExecutionResult replay = await context.ExecuteAsync(plan);
        Assert.Equal(ExecutionOutcome.Completed, replay.Outcome);
        Assert.True(replay.WasReplayed);
        Assert.Single(context.Moves.CommittedOrdinals);
    }

    [Fact]
    public async Task CrashAfterAppliedBeforeOutcomePublicationLeavesIntentForReconciliation()
    {
        OrganizationPlan plan = Plan(2);
        Harness context = new();
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary is
                {
                    Boundary: JournalFaultBoundary.BeforePublish,
                    Record.Kind: ExecutionJournalRecordKind.MoveApplied,
                })
            {
                throw new SimulatedCrashException(
                    "Crash after physical Applied but before durable outcome.");
            }
        };

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => context.ExecuteAsync(plan).AsTask());

        PlanFingerprint fingerprint = Compile(plan).Fingerprint;
        Assert.Equal([0], context.Moves.CommittedOrdinals);
        Assert.Equal([0], context.Moves.ObservedOrdinals);
        Assert.Equal([0], context.Moves.DisposedOrdinals);
        Assert.Equal(
            [ExecutionJournalRecordKind.ExecutionStarted, ExecutionJournalRecordKind.MoveIntent],
            context.Journal.Records(fingerprint).Select(record => record.Kind));

        context.Journal.OnBoundary = null;
        ExecutionResult retry = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, retry.Outcome);
        Assert.Equal(
            [ExecutionItemOutcome.Ambiguous, ExecutionItemOutcome.NotAttempted],
            retry.Items.Select(item => item.Outcome));
        Assert.Equal([0], context.Moves.CommittedOrdinals);
        Assert.Empty(context.Moves.RecoveryObservedOrdinals);
    }

    [Fact]
    public async Task PersistedOutcomeSurvivesAnAfterPublishCrashWithoutAnotherMove()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary.Boundary == JournalFaultBoundary.AfterPublish
                && boundary.Record.Kind == ExecutionJournalRecordKind.MoveApplied)
            {
                throw new SimulatedCrashException("Crash after durable outcome.");
            }
        };

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => context.ExecuteAsync(plan).AsTask());
        context.Journal.OnBoundary = null;

        PlanFingerprint fingerprint = Compile(plan).Fingerprint;
        ExecutionRecoveryResult recovery = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionRecoveryOutcome.Completed, recovery.Outcome);
        Assert.Single(context.Moves.CommittedOrdinals);
        Assert.Empty(context.Moves.RecoveryObservedOrdinals);
    }

    [Fact]
    public async Task CrashAfterDurableOutcomeBeforeNextItemPreservesAppliedPrefixWithoutRetry()
    {
        OrganizationPlan plan = Plan(3);
        Harness context = new();
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary is
                {
                    Boundary: JournalFaultBoundary.AfterPublish,
                    Record.Kind: ExecutionJournalRecordKind.MoveApplied,
                    Record.OperationOrdinal: 0,
                })
            {
                throw new SimulatedCrashException("Crash before the next operation.");
            }
        };

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => context.ExecuteAsync(plan).AsTask());
        context.Journal.OnBoundary = null;
        int commitsAfterCrash = context.Moves.CommittedOrdinals.Count;

        ExecutionResult firstReplay = await context.ExecuteAsync(plan);
        ExecutionResult secondReplay = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, firstReplay.Outcome);
        Assert.Equal(ExecutionOutcome.RecoveryRequired, secondReplay.Outcome);
        Assert.Equal(
            [
                ExecutionItemOutcome.Applied,
                ExecutionItemOutcome.NotAttempted,
                ExecutionItemOutcome.NotAttempted,
            ],
            firstReplay.Items.Select(item => item.Outcome));
        Assert.Equal(commitsAfterCrash, context.Moves.CommittedOrdinals.Count);
        Assert.Equal([0], context.Moves.CommittedOrdinals);
        Assert.Equal(
            [
                ExecutionJournalRecordKind.ExecutionStarted,
                ExecutionJournalRecordKind.MoveIntent,
                ExecutionJournalRecordKind.MoveApplied,
            ],
            context.Journal.Records(firstReplay.Fingerprint!).Select(record => record.Kind));
    }

    [Fact]
    public async Task CrashAfterDurableTerminalReplaysCompletedWithoutAnyPhysicalCall()
    {
        OrganizationPlan plan = Plan(2);
        Harness context = new();
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary is
                {
                    Boundary: JournalFaultBoundary.AfterPublish,
                    Record.Kind: ExecutionJournalRecordKind.ExecutionCompleted,
                })
            {
                throw new SimulatedCrashException("Crash after durable completion.");
            }
        };

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => context.ExecuteAsync(plan).AsTask());
        context.Journal.OnBoundary = null;
        int commitsAfterCrash = context.Moves.CommittedOrdinals.Count;
        int preflightCallsAfterCrash = context.Preflight.CallCount;

        ExecutionResult firstReplay = await context.ExecuteAsync(plan);
        ExecutionResult secondReplay = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Completed, firstReplay.Outcome);
        Assert.Equal(ExecutionOutcome.Completed, secondReplay.Outcome);
        Assert.True(firstReplay.WasReplayed);
        Assert.True(secondReplay.WasReplayed);
        Assert.Equal(commitsAfterCrash, context.Moves.CommittedOrdinals.Count);
        Assert.Equal(preflightCallsAfterCrash, context.Preflight.CallCount);
        Assert.Equal(
            ExecutionJournalRecordKind.ExecutionCompleted,
            context.Journal.Records(firstReplay.Fingerprint!)[^1].Kind);
    }

    [Fact]
    public async Task CrashAfterDurableBeginLeavesARecoverableNoIntentJournal()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary.Boundary == JournalFaultBoundary.AfterBegin)
            {
                throw new SimulatedCrashException("Crash after durable journal begin.");
            }
        };

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => context.ExecuteAsync(plan).AsTask());
        context.Journal.OnBoundary = null;
        PlanFingerprint fingerprint = Compile(plan).Fingerprint;

        ExecutionResult retry = await context.ExecuteAsync(plan);
        ExecutionRecoveryResult recovery = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, retry.Outcome);
        Assert.Equal(ExecutionRecoveryOutcome.Resumable, recovery.Outcome);
        Assert.Equal(0, recovery.NextOperationOrdinal);
        Assert.Empty(context.Moves.CommittedOrdinals);
    }

    [Theory]
    [InlineData(JournalPublishStatus.SequenceConflict)]
    [InlineData(JournalPublishStatus.InvalidJournal)]
    [InlineData(JournalPublishStatus.AlreadyPublished)]
    public async Task InvalidIntentPublicationRequiresRecoveryBeforeCommit(
        JournalPublishStatus publishStatus)
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Journal.PublishOverride = requested =>
            requested.Kind == ExecutionJournalRecordKind.MoveIntent
                ? publishStatus switch
                {
                    JournalPublishStatus.SequenceConflict =>
                        JournalPublishResult.SequenceConflict(),
                    JournalPublishStatus.InvalidJournal =>
                        JournalPublishResult.InvalidJournal(),
                    JournalPublishStatus.AlreadyPublished =>
                        JournalPublishResult.AlreadyPublished(
                            ExecutionJournalRecord.ExecutionStarted(
                                requested.RunId,
                                requested.Fingerprint,
                                requested.Sequence,
                                requested.RecordedAtUtc)),
                    _ => throw new InvalidOperationException(),
                }
                : null;

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(
            [ExecutionProblemKind.InvalidJournal, ExecutionProblemKind.RecoveryRequired],
            result.Problems.Select(problem => problem.Kind));
        Assert.Empty(context.Moves.CommittedOrdinals);
        Assert.Equal(
            [ExecutionJournalRecordKind.ExecutionStarted],
            context.Journal.Records(result.Fingerprint!).Select(record => record.Kind));
    }

    [Fact]
    public async Task ExistingExecutionFaultBeforeAnyMoveReturnsStoppedNotInterrupted()
    {
        OrganizationPlan plan = Plan(2);
        ExecutionManifest manifest = Compile(plan);
        Harness context = new();
        context.Journal.Seed(
            manifest,
            [
                ExecutionJournalRecord.ExecutionStarted(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    0,
                    ExecutionTime),
                ExecutionJournalRecord.ExecutionInterrupted(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    1,
                    ExecutionTime,
                    JournalRecordReasonKind.ExecutionFault),
            ]);

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.Stopped, result.Outcome);
        Assert.All(result.Items, item => Assert.Equal(ExecutionItemOutcome.NotAttempted, item.Outcome));
        Assert.Contains(
            result.Problems,
            problem => problem.Kind == ExecutionProblemKind.JournalFailure);
    }

    [Fact]
    public async Task ExistingExecutionFaultAfterAppliedPrefixReturnsPartialCompletion()
    {
        OrganizationPlan plan = Plan(2);
        ExecutionManifest manifest = Compile(plan);
        Harness context = new();
        PhysicalEntryIdentity identity = TestPhysicalIdentities.File(
            "durable-first",
            volumeIdentity: "volume",
            scheme: "test");
        context.Journal.Seed(
            manifest,
            [
                ExecutionJournalRecord.ExecutionStarted(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    0,
                    ExecutionTime),
                ExecutionJournalRecord.MoveIntent(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    1,
                    ExecutionTime,
                    0,
                    identity),
                ExecutionJournalRecord.MoveApplied(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    2,
                    ExecutionTime,
                    0),
                ExecutionJournalRecord.ExecutionInterrupted(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    3,
                    ExecutionTime,
                    JournalRecordReasonKind.ExecutionFault),
            ]);

        ExecutionResult result = await context.ExecuteAsync(plan);

        Assert.Equal(ExecutionOutcome.PartiallyCompleted, result.Outcome);
        Assert.Equal(
            [ExecutionItemOutcome.Applied, ExecutionItemOutcome.NotAttempted],
            result.Items.Select(item => item.Outcome));
        Assert.Contains(
            result.Problems,
            problem => problem.Kind == ExecutionProblemKind.JournalFailure);
    }

    [Fact]
    public async Task DifferentUnresolvedJournalBlocksANewFingerprint()
    {
        OrganizationPlan firstPlan = Plan(1);
        OrganizationPlan secondPlan = Plan(1, sourceStem: "different");
        Harness context = new();
        context.Moves[0].FaultBeforeStateChange =
            new SimulatedCrashException("Leave the first journal unresolved.");

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => context.ExecuteAsync(firstPlan).AsTask());
        context.Moves[0].FaultBeforeStateChange = null;
        int commits = context.Moves.CommittedOrdinals.Count;

        ExecutionResult blocked = await context.ExecuteAsync(secondPlan);

        Assert.Equal(ExecutionOutcome.RecoveryRequired, blocked.Outcome);
        Assert.All(blocked.Items, item => Assert.Equal(ExecutionItemOutcome.NotAttempted, item.Outcome));
        Assert.Contains(
            blocked.Problems,
            problem => problem.Kind == ExecutionProblemKind.ExecutionAlreadyInProgress);
        Assert.Equal(commits, context.Moves.CommittedOrdinals.Count);
    }

    [Fact]
    public async Task BusyGlobalLeasePreventsExecutionAndRecoveryPorts()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.GlobalLease.NextStatus = GlobalExecutionLeaseAcquisitionStatus.Busy;
        PlanFingerprint fingerprint = Compile(plan).Fingerprint;

        ExecutionResult execution = await context.ExecuteAsync(plan);
        ExecutionRecoveryResult recovery = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionOutcome.Stopped, execution.Outcome);
        Assert.Equal(ExecutionRecoveryOutcome.Busy, recovery.Outcome);
        Assert.Equal(0, context.Journal.ReadCount);
        Assert.Equal(0, context.Journal.BeginCount);
        Assert.Equal(0, context.Journal.PublishCount);
        Assert.Equal(0, context.Preflight.CallCount);
        Assert.Empty(context.Moves.AcquiredOrdinals);
        Assert.Empty(context.Moves.RecoveryObservedOrdinals);
    }

    [Theory]
    [InlineData(
        GlobalExecutionLeaseAcquisitionStatus.Unsupported,
        ExecutionProblemKind.ExecutionLeaseUnsupported)]
    [InlineData(
        GlobalExecutionLeaseAcquisitionStatus.Failed,
        ExecutionProblemKind.ExecutionLeaseAcquisitionFailed)]
    public async Task UnavailableGlobalLeaseFailsStopBeforeJournalOrFilesystemPorts(
        GlobalExecutionLeaseAcquisitionStatus status,
        ExecutionProblemKind expectedProblem)
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.GlobalLease.NextStatus = status;
        PlanFingerprint fingerprint = Compile(plan).Fingerprint;

        ExecutionResult execution = await context.ExecuteAsync(plan);
        ExecutionRecoveryResult recovery = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionOutcome.Stopped, execution.Outcome);
        Assert.Equal(expectedProblem, Assert.Single(execution.Problems).Kind);
        Assert.Equal(ExecutionRecoveryOutcome.LeaseUnavailable, recovery.Outcome);
        Assert.Equal(expectedProblem, Assert.Single(recovery.Problems).Kind);
        Assert.Equal(0, context.Journal.ReadCount);
        Assert.Equal(0, context.Journal.BeginCount);
        Assert.Equal(0, context.Journal.PublishCount);
        Assert.Equal(0, context.Preflight.CallCount);
        Assert.Empty(context.Moves.AcquiredOrdinals);
        Assert.Empty(context.Moves.RecoveryObservedOrdinals);
    }

    [Fact]
    public void PreflightResultSnapshotsCallerListAndExposesAReadOnlyView()
    {
        List<ExecutionPreflightItemResult> callerItems =
        [
            ExecutionPreflightItemResult.Ready(0),
        ];

        ExecutionPreflightResult result = new(callerItems);
        callerItems.Clear();

        Assert.Single(result.Items);
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionPreflightItemResult>)result.Items).Clear());
    }

    [Fact]
    public async Task DisposingAnOldFakeLeaseTwiceCannotReleaseANewerOwner()
    {
        FakeGlobalExecutionLeaseProvider provider = new();
        IGlobalExecutionLease first = Assert.IsAssignableFrom<IGlobalExecutionLease>(
            (await provider.AcquireAsync(
                TimeSpan.Zero,
                TestContext.Current.CancellationToken)).Lease);
        await first.DisposeAsync();
        IGlobalExecutionLease second = Assert.IsAssignableFrom<IGlobalExecutionLease>(
            (await provider.AcquireAsync(
                TimeSpan.Zero,
                TestContext.Current.CancellationToken)).Lease);

        await first.DisposeAsync();
        GlobalExecutionLeaseAcquisition third = await provider.AcquireAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Busy, third.Status);
        Assert.Null(third.Lease);
        await second.DisposeAsync();
    }

    [Fact]
    public void ExecutionResultRejectsUnexplainedStopsAndProblematicNoWork()
    {
        ExecutionManifest manifest = Compile(Plan(1));
        ExecutionManifest empty = Compile(Plan(0));

        Assert.Throws<ArgumentException>(
            () => new ExecutionResult(
                manifest,
                ExecutionTime,
                ExecutionTime,
                ExecutionOutcome.Stopped,
                [new ExecutionItemResult(0, ExecutionItemOutcome.NotAttempted, [])],
                problems: []));
        Assert.Throws<ArgumentException>(
            () => new ExecutionResult(
                empty,
                ExecutionTime,
                ExecutionTime,
                ExecutionOutcome.NoWork,
                items: [],
                problems:
                [
                    new ExecutionProblem(
                        ExecutionProblemKind.JournalFailure,
                        "NoWork cannot carry a journal failure."),
                ]));
        Assert.Throws<ArgumentException>(
            () => new ExecutionResult(
                manifest,
                ExecutionTime,
                ExecutionTime,
                ExecutionOutcome.Interrupted,
                [new ExecutionItemResult(0, ExecutionItemOutcome.NotAttempted, [])],
                problems:
                [
                    new ExecutionProblem(
                        ExecutionProblemKind.JournalFailure,
                        "Interrupted is cancellation-only."),
                ]));
    }

    private static ExecutionProblem Problem(
        ExecutionProblemKind kind,
        string description,
        int ordinal) => new(kind, description, ordinal);

    private static OrganizationPlan Plan(int count, string sourceStem = "file")
    {
        ManifestTestPlan.TestOperation[] operations = Enumerable.Range(0, count)
            .Select(
                index => new ManifestTestPlan.TestOperation(
                    $@"C:\Inbox\{sourceStem}-{index:D2}.txt",
                    new MoveToDirectoryAction(@"C:\Sorted\Text"),
                    $"move-{index}",
                    RuleOrder: index))
            .ToArray();
        return ManifestTestPlan.Create(operations);
    }

    private static ExecutionManifest Compile(OrganizationPlan plan) =>
        Assert.IsType<ExecutionManifest>(ExecutionManifestCompiler.Compile(plan).Manifest);

    private sealed class Harness
    {
        internal Harness()
        {
            Executor = new OrganizationPlanExecutor(
                Preflight,
                Moves,
                Moves,
                Journal,
                GlobalLease,
                new FixedTimeProvider(ExecutionTime));
        }

        internal ScriptedPreflightPort Preflight { get; } = new();

        internal ScriptedMovePort Moves { get; } = new();

        internal ScriptedJournalStore Journal { get; } = new();

        internal FakeGlobalExecutionLeaseProvider GlobalLease { get; } = new();

        internal OrganizationPlanExecutor Executor { get; }

        internal ValueTask<ExecutionResult> ExecuteAsync(OrganizationPlan plan) =>
            Executor.ExecuteAsync(plan, TestContext.Current.CancellationToken);

        internal ValueTask<ExecutionRecoveryResult> RecoverAsync(PlanFingerprint fingerprint) =>
            Executor.RecoverAsync(fingerprint, TestContext.Current.CancellationToken);
    }
}
