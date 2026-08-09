using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Application.Orchestration;
using FileOrganizer.Application.Recovery;
using FileOrganizer.Application.Tests.Execution;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Planning;

namespace FileOrganizer.Application.Tests.Orchestration;

public sealed class OrganizationPlanRecoveryTests
{
    private static readonly DateTimeOffset ExecutionTime =
        ManifestTestPlan.GeneratedAt.AddHours(2);

    [Fact]
    public async Task MissingJournalHasAnExplicitOutcomeAndReadsNoFilesystemPort()
    {
        Harness context = new();
        PlanFingerprint fingerprint = Compile(Plan()).Fingerprint;

        ExecutionRecoveryResult result = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionRecoveryOutcome.NoJournal, result.Outcome);
        Assert.Null(result.Manifest);
        Assert.Empty(context.Moves.RecoveryObservedOrdinals);
        Assert.Empty(context.Moves.CommittedOrdinals);
    }

    [Fact]
    public async Task PendingIntentWithExpectedSourceOnlyBecomesExplicitlyResumable()
    {
        OrganizationPlan plan = Plan();
        Harness context = new();
        PlanFingerprint fingerprint = await LeavePendingIntentAsync(
            context,
            plan,
            afterStateChange: false);

        ExecutionRecoveryResult result = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionRecoveryOutcome.Resumable, result.Outcome);
        Assert.Equal(RecoveryDisposition.NotApplied, result.Resolution?.Disposition);
        Assert.Equal(RecoveryReasonKind.ExpectedEntryAtSourceOnly, result.Resolution?.Reason);
        Assert.Equal(0, result.NextOperationOrdinal);
        Assert.True(result.JournalWasUpdated);
        Assert.Equal([0], context.Moves.RecoveryObservedOrdinals);
        Assert.Single(context.Moves.CommittedOrdinals);
        ExecutionJournalRecord last = context.Journal.Records(fingerprint)[^1];
        Assert.Equal(ExecutionJournalRecordKind.MoveNotApplied, last.Kind);
        Assert.Equal(JournalRecordReasonKind.RecoveryConfirmedNotApplied, last.Reason);
    }

    [Fact]
    public async Task PendingIntentWithExpectedTargetOnlyRecordsAppliedButStillRequiresExplicitResume()
    {
        OrganizationPlan plan = Plan();
        Harness context = new();
        PlanFingerprint fingerprint = await LeavePendingIntentAsync(
            context,
            plan,
            afterStateChange: true);

        ExecutionRecoveryResult result = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionRecoveryOutcome.Resumable, result.Outcome);
        Assert.Equal(RecoveryDisposition.Applied, result.Resolution?.Disposition);
        Assert.Null(result.NextOperationOrdinal);
        Assert.True(result.JournalWasUpdated);
        Assert.DoesNotContain(
            context.Journal.Records(fingerprint),
            record => record.Kind == ExecutionJournalRecordKind.ExecutionCompleted);
        ExecutionJournalState state = ExecutionJournalStateMachine.Replay(
            new ExecutionJournalSnapshot(
                result.Manifest!,
                context.Journal.Records(fingerprint)));
        Assert.Equal(ExecutionJournalStatus.Resumable, state.Status);
    }

    [Fact]
    public async Task RepeatingRecoveryOfResolvedIntentDoesNotObserveOrPublishAgain()
    {
        OrganizationPlan plan = Plan();
        Harness context = new();
        PlanFingerprint fingerprint = await LeavePendingIntentAsync(
            context,
            plan,
            afterStateChange: false);
        ExecutionRecoveryResult first = await context.RecoverAsync(fingerprint);
        int observations = context.Moves.RecoveryObservedOrdinals.Count;
        int publishes = context.Journal.PublishCount;

        ExecutionRecoveryResult second = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionRecoveryOutcome.Resumable, first.Outcome);
        Assert.Equal(ExecutionRecoveryOutcome.Resumable, second.Outcome);
        Assert.Equal(observations, context.Moves.RecoveryObservedOrdinals.Count);
        Assert.Equal(publishes, context.Journal.PublishCount);
        Assert.False(second.JournalWasUpdated);
    }

    [Fact]
    public async Task RecoveryClassifiesDifferentTargetIdentityAsConflict()
    {
        OrganizationPlan plan = Plan();
        Harness context = new();
        PlanFingerprint fingerprint = await LeavePendingIntentAsync(
            context,
            plan,
            afterStateChange: false);
        context.Moves[0].CurrentObservation = new MoveObservation(
            RecoveryEntryObservation.Present(context.Moves[0].Identity),
            RecoveryEntryObservation.Present(
                new EntryIdentity("test", "volume", "different")));

        ExecutionRecoveryResult result = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionRecoveryOutcome.Conflict, result.Outcome);
        Assert.Equal(RecoveryDisposition.Conflict, result.Resolution?.Disposition);
        Assert.Equal(RecoveryReasonKind.TargetOccupiedByDifferentEntry, result.Resolution?.Reason);
        Assert.Equal(
            ExecutionJournalRecordKind.MoveConflict,
            context.Journal.Records(fingerprint)[^1].Kind);
    }

    [Fact]
    public async Task RecoveryTreatsExpectedIdentityAtBothPathsAsAmbiguous()
    {
        OrganizationPlan plan = Plan();
        Harness context = new();
        PlanFingerprint fingerprint = await LeavePendingIntentAsync(
            context,
            plan,
            afterStateChange: false);
        context.Moves[0].CurrentObservation = new MoveObservation(
            RecoveryEntryObservation.Present(context.Moves[0].Identity),
            RecoveryEntryObservation.Present(context.Moves[0].Identity));

        ExecutionRecoveryResult result = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionRecoveryOutcome.Ambiguous, result.Outcome);
        Assert.Equal(RecoveryReasonKind.ExpectedEntryAtBothPaths, result.Resolution?.Reason);
        Assert.Equal(
            ExecutionJournalRecordKind.MoveAmbiguous,
            context.Journal.Records(fingerprint)[^1].Kind);
    }

    [Fact]
    public async Task RecoveryInspectionFailureRequiresManualRecovery()
    {
        OrganizationPlan plan = Plan();
        Harness context = new();
        PlanFingerprint fingerprint = await LeavePendingIntentAsync(
            context,
            plan,
            afterStateChange: false);
        context.Moves[0].CurrentObservation = new MoveObservation(
            RecoveryEntryObservation.InspectionFailed(
                RecoveryInspectionFailureKind.AccessDenied),
            RecoveryEntryObservation.Missing());

        ExecutionRecoveryResult result = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionRecoveryOutcome.ManualRecoveryRequired, result.Outcome);
        Assert.Equal(RecoveryDisposition.ManualRecovery, result.Resolution?.Disposition);
        Assert.Equal(
            ExecutionJournalRecordKind.MoveManualRecovery,
            context.Journal.Records(fingerprint)[^1].Kind);
    }

    [Fact]
    public async Task CancellationDuringRecoveryObservationPublishesNothing()
    {
        OrganizationPlan plan = Plan();
        Harness context = new();
        PlanFingerprint fingerprint = await LeavePendingIntentAsync(
            context,
            plan,
            afterStateChange: false);
        int records = context.Journal.Records(fingerprint).Count;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => context.Executor.RecoverAsync(fingerprint, cancellation.Token).AsTask());

        Assert.Equal(records, context.Journal.Records(fingerprint).Count);
    }

    [Fact]
    public async Task CancellationAfterRecoveryObservationDoesNotPublishAResolution()
    {
        OrganizationPlan plan = Plan();
        Harness context = new();
        PlanFingerprint fingerprint = await LeavePendingIntentAsync(
            context,
            plan,
            afterStateChange: false);
        int records = context.Journal.Records(fingerprint).Count;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        context.Moves[0].OnRecoveryObserved = cancellation.Cancel;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => context.Executor.RecoverAsync(fingerprint, cancellation.Token).AsTask());

        Assert.Equal([0], context.Moves.RecoveryObservedOrdinals);
        Assert.Equal(records, context.Journal.Records(fingerprint).Count);
        Assert.Equal(
            ExecutionJournalRecordKind.MoveIntent,
            context.Journal.Records(fingerprint)[^1].Kind);
    }

    [Fact]
    public async Task RecoveryPublicationFaultBeforeDurabilityCanBeRetriedSafely()
    {
        OrganizationPlan plan = Plan();
        Harness context = new();
        PlanFingerprint fingerprint = await LeavePendingIntentAsync(
            context,
            plan,
            afterStateChange: true);
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary.Boundary == JournalFaultBoundary.BeforePublish
                && boundary.Record.Kind == ExecutionJournalRecordKind.MoveApplied)
            {
                throw new SimulatedCrashException("Recovery publish failed before durability.");
            }
        };

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => context.RecoverAsync(fingerprint).AsTask());
        Assert.Equal(ExecutionJournalRecordKind.MoveIntent, context.Journal.Records(fingerprint)[^1].Kind);

        context.Journal.OnBoundary = null;
        ExecutionRecoveryResult retry = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionRecoveryOutcome.Resumable, retry.Outcome);
        Assert.Equal(ExecutionJournalRecordKind.MoveApplied, context.Journal.Records(fingerprint)[^1].Kind);
        Assert.Single(context.Moves.CommittedOrdinals);
    }

    [Fact]
    public async Task RecoveryPublicationFaultAfterDurabilityIsIdempotentlyObservedOnRetry()
    {
        OrganizationPlan plan = Plan();
        Harness context = new();
        PlanFingerprint fingerprint = await LeavePendingIntentAsync(
            context,
            plan,
            afterStateChange: true);
        context.Journal.OnBoundary = boundary =>
        {
            if (boundary.Boundary == JournalFaultBoundary.AfterPublish
                && boundary.Record.Kind == ExecutionJournalRecordKind.MoveApplied)
            {
                throw new SimulatedCrashException("Recovery publish failed after durability.");
            }
        };

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => context.RecoverAsync(fingerprint).AsTask());
        int observations = context.Moves.RecoveryObservedOrdinals.Count;
        context.Journal.OnBoundary = null;

        ExecutionRecoveryResult retry = await context.RecoverAsync(fingerprint);

        Assert.Equal(ExecutionRecoveryOutcome.Resumable, retry.Outcome);
        Assert.Equal(observations, context.Moves.RecoveryObservedOrdinals.Count);
        Assert.False(retry.JournalWasUpdated);
    }

    [Fact]
    public async Task MismatchedReadSnapshotIsRejectedBeforeObservationOrPublication()
    {
        Harness context = new();
        ExecutionManifest requested = Compile(Plan());
        ExecutionManifest other = Compile(Plan("other"));
        context.Journal.ReadOverride = _ => new ExecutionJournalSnapshot(
            other,
            [
                ExecutionJournalRecord.ExecutionStarted(
                    other.Fingerprint,
                    0,
                    ExecutionTime),
            ]);
        int publishes = context.Journal.PublishCount;

        ExecutionRecoveryResult result = await context.RecoverAsync(
            requested.Fingerprint);

        Assert.Equal(ExecutionRecoveryOutcome.InvalidJournal, result.Outcome);
        Assert.Empty(context.Moves.RecoveryObservedOrdinals);
        Assert.Equal(publishes, context.Journal.PublishCount);
    }

    [Fact]
    public void RecoveryResultSnapshotsProblemsAndExposesAReadOnlyView()
    {
        PlanFingerprint fingerprint = Compile(Plan()).Fingerprint;
        List<ExecutionProblem> callerProblems =
        [
            new ExecutionProblem(
                ExecutionProblemKind.ExecutionAlreadyInProgress,
                "The global lease is busy."),
        ];
        ExecutionRecoveryResult result = new(
            fingerprint,
            ExecutionRecoveryOutcome.Busy,
            problems: callerProblems);

        callerProblems.Clear();

        Assert.Single(result.Problems);
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionProblem>)result.Problems).Clear());
    }

    [Fact]
    public void RecoveryResultRejectsContradictoryImmutableState()
    {
        ExecutionManifest manifest = Compile(Plan());
        ExecutionManifest other = Compile(Plan("other-shape"));
        RecoveryResolution conflict = MoveRecoveryStateMachine.Classify(
            new EntryIdentity("test", "volume", "expected"),
            RecoveryEntryObservation.Present(
                new EntryIdentity("test", "volume", "changed")),
            RecoveryEntryObservation.Missing());

        Assert.Throws<ArgumentException>(
            () => new ExecutionRecoveryResult(
                manifest.Fingerprint,
                ExecutionRecoveryOutcome.Completed,
                other));
        Assert.Throws<ArgumentException>(
            () => new ExecutionRecoveryResult(
                manifest.Fingerprint,
                ExecutionRecoveryOutcome.Busy));
        Assert.Throws<ArgumentException>(
            () => new ExecutionRecoveryResult(
                manifest.Fingerprint,
                ExecutionRecoveryOutcome.Completed,
                manifest,
                nextOperationOrdinal: 0));
        Assert.Throws<ArgumentException>(
            () => new ExecutionRecoveryResult(
                manifest.Fingerprint,
                ExecutionRecoveryOutcome.Resumable,
                manifest,
                conflict));
        Assert.Throws<ArgumentException>(
            () => new ExecutionRecoveryResult(
                manifest.Fingerprint,
                ExecutionRecoveryOutcome.Interrupted,
                manifest,
                problems:
                [
                    new ExecutionProblem(
                        ExecutionProblemKind.RecoveryRequired,
                        "This is not a journal-failure problem."),
                ]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExecutionRecoveryResult(
                manifest.Fingerprint,
                ExecutionRecoveryOutcome.Resumable,
                manifest,
                nextOperationOrdinal: manifest.Items.Count));

        ExecutionManifest twoItems = Compile(
            ManifestTestPlan.Create(
            [
                new ManifestTestPlan.TestOperation(
                    @"C:\Inbox\first.txt",
                    new MoveToDirectoryAction(@"C:\Sorted\Text"),
                    "first"),
                new ManifestTestPlan.TestOperation(
                    @"C:\Inbox\second.txt",
                    new MoveToDirectoryAction(@"C:\Sorted\Text"),
                    "second"),
            ]));
        Assert.Throws<ArgumentException>(
            () => new ExecutionRecoveryResult(
                twoItems.Fingerprint,
                ExecutionRecoveryOutcome.Conflict,
                twoItems,
                nextOperationOrdinal: 0,
                problems:
                [
                    new ExecutionProblem(
                        ExecutionProblemKind.DestinationConflict,
                        "The problem identifies the wrong item.",
                        operationOrdinal: 1),
                ]));
        Assert.Throws<ArgumentException>(
            () => new ExecutionRecoveryResult(
                manifest.Fingerprint,
                ExecutionRecoveryOutcome.Ambiguous,
                manifest,
                problems:
                [
                    new ExecutionProblem(
                        ExecutionProblemKind.MoveOutcomeAmbiguous,
                        "An ambiguous outcome requires an affected ordinal."),
                ]));
    }

    private static async Task<PlanFingerprint> LeavePendingIntentAsync(
        Harness context,
        OrganizationPlan plan,
        bool afterStateChange)
    {
        context.Moves[0].FaultBeforeStateChange = afterStateChange
            ? null
            : new SimulatedCrashException("Crash before the scripted state change.");
        context.Moves[0].FaultAfterStateChange = afterStateChange
            ? new SimulatedCrashException("Crash after the scripted state change.")
            : null;

        await Assert.ThrowsAsync<SimulatedCrashException>(
            () => context.ExecuteAsync(plan).AsTask());
        context.Moves[0].FaultBeforeStateChange = null;
        context.Moves[0].FaultAfterStateChange = null;
        return Compile(plan).Fingerprint;
    }

    private static OrganizationPlan Plan(string sourceStem = "recover") =>
        ManifestTestPlan.Create(
        [
            new ManifestTestPlan.TestOperation(
                $@"C:\Inbox\{sourceStem}.txt",
                new MoveToDirectoryAction(@"C:\Sorted\Text"),
                "move"),
        ]);

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
