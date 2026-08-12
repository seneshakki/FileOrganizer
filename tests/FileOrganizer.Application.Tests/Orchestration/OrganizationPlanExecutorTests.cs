using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Application.Orchestration;
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
    public async Task CancellationDuringFinalCommitStillPublishesCompletion()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        context.Moves[0].OnCommitted = cancellation.Cancel;

        ExecutionResult result = await context.Executor.ExecuteAsync(plan, cancellation.Token);

        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(ExecutionItemOutcome.Applied, Assert.Single(result.Items).Outcome);
        Assert.Equal([0], context.Moves.ObservedOrdinals);
        Assert.Equal(
            ExecutionJournalRecordKind.ExecutionCompleted,
            context.Journal.Records(result.Fingerprint!)[^1].Kind);
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

    [Fact]
    public async Task UnrelatedAlreadyPublishedRecordFailsBeforeCommit()
    {
        OrganizationPlan plan = Plan(1);
        Harness context = new();
        context.Journal.PublishOverride = requested =>
            requested.Kind == ExecutionJournalRecordKind.MoveIntent
                ? JournalPublishResult.AlreadyPublished(
                    ExecutionJournalRecord.ExecutionStarted(
                        requested.RunId,
                        requested.Fingerprint,
                        requested.Sequence,
                        requested.RecordedAtUtc))
                : null;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.ExecuteAsync(plan).AsTask());

        Assert.Empty(context.Moves.CommittedOrdinals);
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
        context.GlobalLease.IsAvailable = false;
        PlanFingerprint fingerprint = Compile(plan).Fingerprint;

        ExecutionResult execution = await context.ExecuteAsync(plan);
        ExecutionRecoveryResult recovery = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionOutcome.Stopped, execution.Outcome);
        Assert.Equal(ExecutionRecoveryOutcome.Busy, recovery.Outcome);
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
            await provider.TryAcquireAsync(TestContext.Current.CancellationToken));
        await first.DisposeAsync();
        IGlobalExecutionLease second = Assert.IsAssignableFrom<IGlobalExecutionLease>(
            await provider.TryAcquireAsync(TestContext.Current.CancellationToken));

        await first.DisposeAsync();
        IGlobalExecutionLease? third = await provider.TryAcquireAsync(
            TestContext.Current.CancellationToken);

        Assert.Null(third);
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
