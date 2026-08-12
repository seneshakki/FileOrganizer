using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Tests.Execution;
using FileOrganizer.Core.Physical;
using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Application.Tests.Journaling;

public sealed class ExecutionJournalStateMachineTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 9, 9, 0, 0, TimeSpan.Zero);

    private static readonly PhysicalEntryIdentity ExpectedIdentity =
        TestPhysicalIdentities.File("entry", "volume", "test-id-v1");

    [Fact]
    public void ReplaysACompleteExecutionInManifestOrder()
    {
        ExecutionManifest manifest = JournalTestManifest.Create(operationCount: 2);
        ExecutionJournalRecord[] records =
        [
            Start(manifest, 0),
            Intent(manifest, 1, 0),
            Applied(manifest, 2, 0),
            Intent(manifest, 3, 1),
            Applied(manifest, 4, 1),
            ExecutionJournalRecord.ExecutionCompleted(
                TestExecutionRuns.Id,
                manifest.Fingerprint,
                5,
                At(5)),
        ];

        ExecutionJournalState state = ExecutionJournalStateMachine.Replay(
            manifest,
            records);

        Assert.Equal(ExecutionJournalStatus.Completed, state.Status);
        Assert.True(state.IsTerminal);
        Assert.Null(state.ActiveOperationOrdinal);
        Assert.Equal(6, state.NextSequence);
        Assert.All(
            state.Operations,
            operation => Assert.Equal(JournalOperationStatus.Applied, operation.Status));
        Assert.All(
            state.Operations,
            operation => Assert.Equal(ExpectedIdentity, operation.ExpectedEntryIdentity));
        Assert.Equal([2L, 4L], state.Operations.Select(operation => operation.LastRecordSequence));
    }

    [Fact]
    public void EmptyStreamAndPendingIntentRemainExplicit()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();

        ExecutionJournalState empty = ExecutionJournalStateMachine.Replay(
            manifest,
            []);
        ExecutionJournalState pending = ExecutionJournalStateMachine.Replay(
            manifest,
            [Start(manifest, 0), Intent(manifest, 1, 0)]);

        Assert.Equal(ExecutionJournalStatus.NotStarted, empty.Status);
        Assert.Equal(JournalOperationStatus.NotStarted, Assert.Single(empty.Operations).Status);
        Assert.Equal(ExecutionJournalStatus.AwaitingRecovery, pending.Status);
        Assert.Equal(0, pending.ActiveOperationOrdinal);
        Assert.Equal(
            JournalOperationStatus.IntentPublished,
            Assert.Single(pending.Operations).Status);
        Assert.Equal(
            ExpectedIdentity,
            Assert.Single(pending.Operations).ExpectedEntryIdentity);
    }

    [Fact]
    public void ExistingDurableSnapshotCannotOmitItsAtomicStartRecord()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalSnapshot invalidSnapshot = new(TestExecutionRuns.Id, manifest, []);

        InvalidJournalSequenceException exception =
            Assert.Throws<InvalidJournalSequenceException>(
                () => ExecutionJournalStateMachine.Replay(invalidSnapshot));

        Assert.Equal(JournalSequenceProblemKind.StartRequired, exception.ProblemKind);
    }

    [Fact]
    public void SnapshotReplayUsesOnlyTheManifestStoredInThatSnapshot()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalSnapshot snapshot = new(
            TestExecutionRuns.Id,
            manifest,
            [Start(manifest, 0)]);

        ExecutionJournalState state = ExecutionJournalStateMachine.Replay(snapshot);

        Assert.Same(manifest, snapshot.Manifest);
        Assert.Equal(manifest.Fingerprint, state.Fingerprint);
        Assert.Equal(ExecutionJournalStatus.InProgress, state.Status);
    }

    [Fact]
    public void ReducedOperationStateCannotBeMutatedThroughItsPublicView()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        List<ExecutionJournalRecord> callerRecords =
        [
            Start(manifest, 0),
            Intent(manifest, 1, 0),
        ];

        ExecutionJournalState state = ExecutionJournalStateMachine.Replay(
            manifest,
            callerRecords);
        callerRecords.Clear();

        Assert.Equal(ExecutionJournalStatus.AwaitingRecovery, state.Status);
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionJournalOperationState>)state.Operations).Clear());
    }

    [Theory]
    [InlineData(ExecutionJournalRecordKind.MoveNotApplied, JournalOperationStatus.NotApplied, ExecutionJournalStatus.Resumable)]
    [InlineData(ExecutionJournalRecordKind.MoveConflict, JournalOperationStatus.Conflict, ExecutionJournalStatus.Conflict)]
    [InlineData(ExecutionJournalRecordKind.MoveAmbiguous, JournalOperationStatus.Ambiguous, ExecutionJournalStatus.ManualRecovery)]
    [InlineData(ExecutionJournalRecordKind.MoveManualRecovery, JournalOperationStatus.ManualRecovery, ExecutionJournalStatus.ManualRecovery)]
    public void PreservesExactNonAppliedOperationStates(
        ExecutionJournalRecordKind kind,
        JournalOperationStatus operationStatus,
        ExecutionJournalStatus executionStatus)
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalRecord outcome = Outcome(manifest, kind);

        ExecutionJournalState state = ExecutionJournalStateMachine.Replay(
            manifest,
            [Start(manifest, 0), Intent(manifest, 1, 0), outcome]);

        Assert.Equal(executionStatus, state.Status);
        Assert.Equal(operationStatus, Assert.Single(state.Operations).Status);
        Assert.Equal(
            executionStatus != ExecutionJournalStatus.Resumable,
            state.IsTerminal);
    }

    [Theory]
    [InlineData(JournalRecordReasonKind.SourceRevalidationFailed)]
    [InlineData(JournalRecordReasonKind.DestinationRevalidationFailed)]
    public void PostIntentRevalidationFailureIsDurablyBlocked(
        JournalRecordReasonKind reason)
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalRecord blocked = ExecutionJournalRecord.MoveNotApplied(
            TestExecutionRuns.Id,
            manifest.Fingerprint,
            2,
            At(2),
            0,
            reason);

        ExecutionJournalState state = ExecutionJournalStateMachine.Replay(
            manifest,
            [Start(manifest, 0), Intent(manifest, 1, 0), blocked]);

        Assert.Equal(ExecutionJournalStatus.Blocked, state.Status);
        Assert.True(state.IsTerminal);
        Assert.Equal(JournalOperationStatus.Blocked, Assert.Single(state.Operations).Status);
        Assert.Equal(reason, Assert.Single(state.Operations).Reason);
    }

    [Fact]
    public void PostIntentRevalidationBlockRejectsEveryJournalTail()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalRecord blocked = ExecutionJournalRecord.MoveNotApplied(
            TestExecutionRuns.Id,
            manifest.Fingerprint,
            2,
            At(2),
            0,
            JournalRecordReasonKind.SourceRevalidationFailed);

        InvalidJournalSequenceException exception =
            Assert.Throws<InvalidJournalSequenceException>(
                () => ExecutionJournalStateMachine.Replay(
                    manifest,
                    [
                        Start(manifest, 0),
                        Intent(manifest, 1, 0),
                        blocked,
                        ExecutionJournalRecord.ExecutionResumed(
                            TestExecutionRuns.Id,
                            manifest.Fingerprint,
                            3,
                            At(3)),
                    ]));

        Assert.Equal(JournalSequenceProblemKind.RecordAfterTerminal, exception.ProblemKind);
    }

    [Theory]
    [InlineData(JournalRecordReasonKind.SourceRevalidationFailed, JournalOperationStatus.Blocked, ExecutionJournalStatus.Blocked)]
    [InlineData(JournalRecordReasonKind.DestinationRevalidationFailed, JournalOperationStatus.Blocked, ExecutionJournalStatus.Blocked)]
    [InlineData(JournalRecordReasonKind.DestinationConflict, JournalOperationStatus.Conflict, ExecutionJournalStatus.Conflict)]
    public void RecordsPreIntentStopsWithoutClaimingAMoveWasAttempted(
        JournalRecordReasonKind reason,
        JournalOperationStatus operationStatus,
        ExecutionJournalStatus executionStatus)
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalRecord stopped = ExecutionJournalRecord.ExecutionStopped(
            TestExecutionRuns.Id,
            manifest.Fingerprint,
            1,
            At(1),
            0,
            reason);

        ExecutionJournalState state = ExecutionJournalStateMachine.Replay(
            manifest,
            [Start(manifest, 0), stopped]);

        ExecutionJournalOperationState operation = Assert.Single(state.Operations);
        Assert.Equal(operationStatus, operation.Status);
        Assert.Equal(reason, operation.Reason);
        Assert.Equal(executionStatus, state.Status);
    }

    [Fact]
    public void RejectsASequenceGapAndFingerprintMismatchPrecisely()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        InvalidJournalSequenceException gap = Assert.Throws<InvalidJournalSequenceException>(
            () => ExecutionJournalStateMachine.Replay(
                manifest,
                [ExecutionJournalRecord.ExecutionStarted(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    1,
                    At(0))]));
        InvalidJournalSequenceException mismatch = Assert.Throws<InvalidJournalSequenceException>(
            () => ExecutionJournalStateMachine.Replay(
                manifest,
                [ExecutionJournalRecord.ExecutionStarted(
                    TestExecutionRuns.Id,
                    new PlanFingerprint(new string('f', 64)),
                    0,
                    At(0))]));

        Assert.Equal(JournalSequenceProblemKind.SequenceGap, gap.ProblemKind);
        Assert.Equal(JournalSequenceProblemKind.FingerprintMismatch, mismatch.ProblemKind);
    }

    [Fact]
    public void RejectsMixedRunOwnershipInTheEnumerableReplayOverload()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalRecord intent = ExecutionJournalRecord.MoveIntent(
            new ExecutionRunId("11111111111111111111111111111111"),
            manifest.Fingerprint,
            1,
            At(1),
            0,
            manifest.Items[0].SourcePrecondition.PhysicalIdentity!);

        InvalidJournalSequenceException exception = Assert.Throws<InvalidJournalSequenceException>(
            () => ExecutionJournalStateMachine.Replay(
                manifest,
                [Start(manifest, 0), intent]));

        Assert.Equal(JournalSequenceProblemKind.RunMismatch, exception.ProblemKind);
    }

    [Fact]
    public void RejectsOutcomeWithoutIntentAndOutOfOrderIntent()
    {
        ExecutionManifest manifest = JournalTestManifest.Create(operationCount: 2);
        InvalidJournalSequenceException missingIntent = Assert.Throws<InvalidJournalSequenceException>(
            () => ExecutionJournalStateMachine.Replay(
                manifest,
                [Start(manifest, 0), Applied(manifest, 1, 0)]));
        InvalidJournalSequenceException wrongOrdinal = Assert.Throws<InvalidJournalSequenceException>(
            () => ExecutionJournalStateMachine.Replay(
                manifest,
                [Start(manifest, 0), Intent(manifest, 1, 1)]));

        Assert.Equal(JournalSequenceProblemKind.OperationIntentRequired, missingIntent.ProblemKind);
        Assert.Equal(JournalSequenceProblemKind.UnexpectedOperationOrdinal, wrongOrdinal.ProblemKind);
    }

    [Fact]
    public void RejectsCompletionBeforeAllAppliedAndAnyTerminalTail()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        InvalidJournalSequenceException premature = Assert.Throws<InvalidJournalSequenceException>(
            () => ExecutionJournalStateMachine.Replay(
                manifest,
                [
                    Start(manifest, 0),
                    ExecutionJournalRecord.ExecutionCompleted(
                        TestExecutionRuns.Id,
                        manifest.Fingerprint,
                        1,
                        At(1)),
                ]));
        InvalidJournalSequenceException tail = Assert.Throws<InvalidJournalSequenceException>(
            () => ExecutionJournalStateMachine.Replay(
                manifest,
                [
                    Start(manifest, 0),
                    ExecutionJournalRecord.ExecutionInterrupted(
                        TestExecutionRuns.Id,
                        manifest.Fingerprint,
                        1,
                        At(1),
                        JournalRecordReasonKind.ExecutionFault),
                    Intent(manifest, 2, 0),
                ]));

        Assert.Equal(JournalSequenceProblemKind.CompletionBeforeAllApplied, premature.ProblemKind);
        Assert.Equal(JournalSequenceProblemKind.RecordAfterTerminal, tail.ProblemKind);
    }

    [Fact]
    public void UsesSequenceRatherThanWallClockForOrderingAndRejectsPendingInterruption()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalState backwardsClock = ExecutionJournalStateMachine.Replay(
            manifest,
            [
                Start(manifest, 0),
                ExecutionJournalRecord.MoveIntent(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    1,
                    StartedAt.AddSeconds(-1),
                    0,
                    ExpectedIdentity),
            ]);
        InvalidJournalSequenceException pending = Assert.Throws<InvalidJournalSequenceException>(
            () => ExecutionJournalStateMachine.Replay(
                manifest,
                [
                    Start(manifest, 0),
                    Intent(manifest, 1, 0),
                    ExecutionJournalRecord.ExecutionInterrupted(
                        TestExecutionRuns.Id,
                        manifest.Fingerprint,
                        2,
                        At(2),
                        JournalRecordReasonKind.CancellationRequested),
                ]));

        Assert.Equal(ExecutionJournalStatus.AwaitingRecovery, backwardsClock.Status);
        Assert.Equal(JournalSequenceProblemKind.InterruptionDuringPendingIntent, pending.ProblemKind);
    }

    [Fact]
    public void RequiresAnExplicitResumeBeforeRetryingAProvenNotAppliedMove()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalRecord notApplied = ExecutionJournalRecord.MoveNotApplied(
            TestExecutionRuns.Id,
            manifest.Fingerprint,
            2,
            At(2),
            0,
            JournalRecordReasonKind.RecoveryConfirmedNotApplied);
        ExecutionJournalRecord retryWithoutResume = Intent(manifest, 3, 0);
        ExecutionJournalRecord retryAfterResume = Intent(manifest, 4, 0);

        InvalidJournalSequenceException missingResume =
            Assert.Throws<InvalidJournalSequenceException>(
                () => ExecutionJournalStateMachine.Replay(
                    manifest,
                    [Start(manifest, 0), Intent(manifest, 1, 0), notApplied, retryWithoutResume]));
        ExecutionJournalState resumed = ExecutionJournalStateMachine.Replay(
            manifest,
            [
                Start(manifest, 0),
                Intent(manifest, 1, 0),
                notApplied,
                ExecutionJournalRecord.ExecutionResumed(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    3,
                    At(3)),
                retryAfterResume,
            ]);

        Assert.Equal(JournalSequenceProblemKind.ResumeRequired, missingResume.ProblemKind);
        Assert.Equal(ExecutionJournalStatus.AwaitingRecovery, resumed.Status);
        Assert.Equal(JournalOperationStatus.IntentPublished, Assert.Single(resumed.Operations).Status);
    }

    [Fact]
    public void RecoveryConfirmedAppliedPausesBeforeTheNextMove()
    {
        ExecutionManifest manifest = JournalTestManifest.Create(operationCount: 2);
        ExecutionJournalRecord recoveredApplied = ExecutionJournalRecord.MoveApplied(
            TestExecutionRuns.Id,
            manifest.Fingerprint,
            2,
            At(2),
            0,
            JournalRecordReasonKind.RecoveryConfirmedApplied);
        ExecutionJournalRecord nextIntentWithoutResume = Intent(manifest, 3, 1);

        ExecutionJournalState paused = ExecutionJournalStateMachine.Replay(
            manifest,
            [Start(manifest, 0), Intent(manifest, 1, 0), recoveredApplied]);
        InvalidJournalSequenceException exception =
            Assert.Throws<InvalidJournalSequenceException>(
                () => ExecutionJournalStateMachine.Replay(
                    manifest,
                    [
                        Start(manifest, 0),
                        Intent(manifest, 1, 0),
                        recoveredApplied,
                        nextIntentWithoutResume,
                    ]));
        ExecutionJournalState resumed = ExecutionJournalStateMachine.Replay(
            manifest,
            [
                Start(manifest, 0),
                Intent(manifest, 1, 0),
                recoveredApplied,
                ExecutionJournalRecord.ExecutionResumed(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    3,
                    At(3)),
                Intent(manifest, 4, 1),
            ]);

        Assert.Equal(ExecutionJournalStatus.Resumable, paused.Status);
        Assert.True(paused.CanResume);
        Assert.Equal(JournalSequenceProblemKind.ResumeRequired, exception.ProblemKind);
        Assert.Equal(ExecutionJournalStatus.AwaitingRecovery, resumed.Status);
        Assert.Equal(1, resumed.ActiveOperationOrdinal);
    }

    [Fact]
    public void LastRecoveryConfirmedAppliedRequiresResumeBeforeCompletion()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalRecord recoveredApplied = ExecutionJournalRecord.MoveApplied(
            TestExecutionRuns.Id,
            manifest.Fingerprint,
            2,
            At(2),
            0,
            JournalRecordReasonKind.RecoveryConfirmedApplied);
        ExecutionJournalRecord completionWithoutResume =
            ExecutionJournalRecord.ExecutionCompleted(
                TestExecutionRuns.Id,
                manifest.Fingerprint,
                3,
                At(3));

        InvalidJournalSequenceException exception =
            Assert.Throws<InvalidJournalSequenceException>(
                () => ExecutionJournalStateMachine.Replay(
                    manifest,
                    [
                        Start(manifest, 0),
                        Intent(manifest, 1, 0),
                        recoveredApplied,
                        completionWithoutResume,
                    ]));
        ExecutionJournalState completed = ExecutionJournalStateMachine.Replay(
            manifest,
            [
                Start(manifest, 0),
                Intent(manifest, 1, 0),
                recoveredApplied,
                ExecutionJournalRecord.ExecutionResumed(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    3,
                    At(3)),
                ExecutionJournalRecord.ExecutionCompleted(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    4,
                    At(4)),
            ]);

        Assert.Equal(JournalSequenceProblemKind.ResumeRequired, exception.ProblemKind);
        Assert.Equal(ExecutionJournalStatus.Completed, completed.Status);
    }

    [Fact]
    public void OnlySafelyClosedHistoriesReleaseGlobalBeginOwnership()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalRecord start = Start(manifest, 0);
        ExecutionJournalRecord intent = Intent(manifest, 1, 0);

        ExecutionJournalState inProgress = ExecutionJournalStateMachine.Replay(
            manifest,
            [start]);
        ExecutionJournalState awaitingRecovery = ExecutionJournalStateMachine.Replay(
            manifest,
            [start, intent]);
        ExecutionJournalState resumable = ExecutionJournalStateMachine.Replay(
            manifest,
            [
                start,
                intent,
                ExecutionJournalRecord.MoveNotApplied(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    2,
                    At(2),
                    0,
                    JournalRecordReasonKind.RecoveryConfirmedNotApplied),
            ]);
        ExecutionJournalState manualRecovery = ExecutionJournalStateMachine.Replay(
            manifest,
            [
                start,
                intent,
                ExecutionJournalRecord.MoveManualRecovery(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    2,
                    At(2),
                    0,
                    JournalRecordReasonKind.RecoveryInspectionFailed),
            ]);
        ExecutionJournalState blocked = ExecutionJournalStateMachine.Replay(
            manifest,
            [
                start,
                ExecutionJournalRecord.ExecutionStopped(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    1,
                    At(1),
                    0,
                    JournalRecordReasonKind.SourceRevalidationFailed),
            ]);
        ExecutionJournalState conflict = ExecutionJournalStateMachine.Replay(
            manifest,
            [
                start,
                ExecutionJournalRecord.ExecutionStopped(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    1,
                    At(1),
                    0,
                    JournalRecordReasonKind.DestinationConflict),
            ]);
        ExecutionJournalState interrupted = ExecutionJournalStateMachine.Replay(
            manifest,
            [
                start,
                ExecutionJournalRecord.ExecutionInterrupted(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    1,
                    At(1),
                    JournalRecordReasonKind.ExecutionFault),
            ]);
        ExecutionJournalState completed = ExecutionJournalStateMachine.Replay(
            manifest,
            [
                start,
                intent,
                Applied(manifest, 2, 0),
                ExecutionJournalRecord.ExecutionCompleted(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    3,
                    At(3)),
            ]);

        Assert.False(inProgress.ReleasesGlobalExecutionOwnership);
        Assert.False(awaitingRecovery.ReleasesGlobalExecutionOwnership);
        Assert.False(resumable.ReleasesGlobalExecutionOwnership);
        Assert.True(manualRecovery.IsTerminal);
        Assert.False(manualRecovery.ReleasesGlobalExecutionOwnership);
        Assert.True(blocked.ReleasesGlobalExecutionOwnership);
        Assert.True(conflict.ReleasesGlobalExecutionOwnership);
        Assert.True(interrupted.ReleasesGlobalExecutionOwnership);
        Assert.True(completed.ReleasesGlobalExecutionOwnership);
    }

    private static ExecutionJournalRecord Start(ExecutionManifest manifest, long sequence) =>
        ExecutionJournalRecord.ExecutionStarted(
            TestExecutionRuns.Id,
            manifest.Fingerprint,
            sequence,
            At(sequence));

    private static ExecutionJournalRecord Intent(
        ExecutionManifest manifest,
        long sequence,
        int ordinal) =>
        ExecutionJournalRecord.MoveIntent(
            TestExecutionRuns.Id,
            manifest.Fingerprint,
            sequence,
            At(sequence),
            ordinal,
            ExpectedIdentity);

    private static ExecutionJournalRecord Applied(
        ExecutionManifest manifest,
        long sequence,
        int ordinal) =>
        ExecutionJournalRecord.MoveApplied(
            TestExecutionRuns.Id,
            manifest.Fingerprint,
            sequence,
            At(sequence),
            ordinal);

    private static ExecutionJournalRecord Outcome(
        ExecutionManifest manifest,
        ExecutionJournalRecordKind kind) =>
        kind switch
        {
            ExecutionJournalRecordKind.MoveNotApplied => ExecutionJournalRecord.MoveNotApplied(
                TestExecutionRuns.Id,
                manifest.Fingerprint,
                2,
                At(2),
                0,
                JournalRecordReasonKind.MoveDidNotApply),
            ExecutionJournalRecordKind.MoveConflict => ExecutionJournalRecord.MoveConflict(
                TestExecutionRuns.Id,
                manifest.Fingerprint,
                2,
                At(2),
                0,
                JournalRecordReasonKind.DestinationConflict),
            ExecutionJournalRecordKind.MoveAmbiguous => ExecutionJournalRecord.MoveAmbiguous(
                TestExecutionRuns.Id,
                manifest.Fingerprint,
                2,
                At(2),
                0,
                JournalRecordReasonKind.RecoveryBothPathsMissing),
            ExecutionJournalRecordKind.MoveManualRecovery => ExecutionJournalRecord.MoveManualRecovery(
                TestExecutionRuns.Id,
                manifest.Fingerprint,
                2,
                At(2),
                0,
                JournalRecordReasonKind.RecoveryInspectionFailed),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static DateTimeOffset At(long sequence) => StartedAt.AddSeconds(sequence);
}
