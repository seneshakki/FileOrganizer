using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Application.Orchestration;
using FileOrganizer.Application.Tests.Execution;
using FileOrganizer.Application.Tests.Journaling;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Tests.Orchestration;

public sealed class ExecutionJournalRecoveryInspectorTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly PhysicalEntryIdentity ExpectedIdentity =
        TestPhysicalIdentities.File("entry", "volume", "test-id-v1");

    [Fact]
    public async Task AbsentJournalRemainsDistinctFromInvalidDurableInput()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionRecoveryResult absent = await InspectAsync(
            manifest.Fingerprint,
            new ExecutionJournalReadResult(ExecutionJournalReadStatus.NotFound));
        ExecutionJournalReadProblem invalidProblem = new(
            ExecutionJournalReadProblemKind.PayloadIntegrityViolation,
            recordSequence: 3);
        ExecutionRecoveryResult invalid = await InspectAsync(
            manifest.Fingerprint,
            new ExecutionJournalReadResult(
                ExecutionJournalReadStatus.Invalid,
                problems: [invalidProblem]));

        Assert.Equal(ExecutionRecoveryOutcome.NoJournal, absent.Outcome);
        Assert.Null(absent.Manifest);
        Assert.Null(absent.JournalState);
        Assert.Empty(absent.Problems);
        Assert.Equal(ExecutionRecoveryOutcome.InvalidJournal, invalid.Outcome);
        Assert.Null(invalid.Manifest);
        Assert.Null(invalid.JournalState);
        Assert.Equal(ExecutionProblemKind.InvalidJournal, Assert.Single(invalid.Problems).Kind);
        Assert.Equal(invalidProblem, Assert.Single(invalid.JournalProblems));
    }

    [Fact]
    public async Task GenesisOnlyIsResumableWithoutClaimingAnOperationWasAttempted()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionRecoveryResult result = await InspectAsync(
            manifest.Fingerprint,
            Loaded(manifest, [Start(manifest, 0)]));

        Assert.Equal(ExecutionRecoveryOutcome.Resumable, result.Outcome);
        Assert.Equal(0, result.NextOperationOrdinal);
        Assert.Equal(ExecutionJournalStatus.InProgress, result.JournalState!.Status);
        Assert.Equal(
            JournalOperationStatus.NotStarted,
            Assert.Single(result.JournalState.Operations).Status);
        Assert.False(result.JournalWasUpdated);
    }

    [Fact]
    public async Task PendingIntentRequiresPhysicalReconciliationWithoutPerformingIt()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionRecoveryResult result = await InspectAsync(
            manifest.Fingerprint,
            Loaded(manifest, [Start(manifest, 0), Intent(manifest, 1, 0)]));

        Assert.Equal(ExecutionRecoveryOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(0, result.NextOperationOrdinal);
        Assert.Equal(ExecutionJournalStatus.AwaitingRecovery, result.JournalState!.Status);
        Assert.Equal(0, result.JournalState.ActiveOperationOrdinal);
        Assert.Equal(ExecutionProblemKind.RecoveryRequired, Assert.Single(result.Problems).Kind);
        Assert.False(result.JournalWasUpdated);
    }

    [Theory]
    [InlineData(true, JournalOperationStatus.Applied, null)]
    [InlineData(false, JournalOperationStatus.NotApplied, 0)]
    public async Task DurableAppliedAndNotAppliedOutcomesRemainDistinct(
        bool applied,
        JournalOperationStatus expectedStatus,
        int? nextOrdinal)
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalRecord outcome = applied
            ? ExecutionJournalRecord.MoveApplied(
                TestExecutionRuns.Id,
                manifest.Fingerprint,
                2,
                At(2),
                0)
            : ExecutionJournalRecord.MoveNotApplied(
                TestExecutionRuns.Id,
                manifest.Fingerprint,
                2,
                At(2),
                0,
                JournalRecordReasonKind.MoveDidNotApply);

        ExecutionRecoveryResult result = await InspectAsync(
            manifest.Fingerprint,
            Loaded(manifest, [Start(manifest, 0), Intent(manifest, 1, 0), outcome]));

        Assert.Equal(ExecutionRecoveryOutcome.Resumable, result.Outcome);
        Assert.Equal(nextOrdinal, result.NextOperationOrdinal);
        Assert.Equal(expectedStatus, Assert.Single(result.JournalState!.Operations).Status);
        Assert.False(result.JournalWasUpdated);
    }

    [Theory]
    [InlineData(ExecutionJournalRecordKind.MoveAmbiguous, ExecutionRecoveryOutcome.Ambiguous)]
    [InlineData(ExecutionJournalRecordKind.MoveManualRecovery, ExecutionRecoveryOutcome.ManualRecoveryRequired)]
    public async Task DurableUnresolvedOutcomesNeverBecomeResumable(
        ExecutionJournalRecordKind kind,
        ExecutionRecoveryOutcome expectedOutcome)
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalRecord outcome = kind switch
        {
            ExecutionJournalRecordKind.MoveAmbiguous => ExecutionJournalRecord.MoveAmbiguous(
                TestExecutionRuns.Id,
                manifest.Fingerprint,
                2,
                At(2),
                0,
                JournalRecordReasonKind.RecoveryBothPathsMissing),
            ExecutionJournalRecordKind.MoveManualRecovery =>
                ExecutionJournalRecord.MoveManualRecovery(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    2,
                    At(2),
                    0,
                    JournalRecordReasonKind.RecoveryInspectionFailed),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        ExecutionRecoveryResult result = await InspectAsync(
            manifest.Fingerprint,
            Loaded(manifest, [Start(manifest, 0), Intent(manifest, 1, 0), outcome]));

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(0, result.NextOperationOrdinal);
        Assert.False(result.JournalWasUpdated);
    }

    [Fact]
    public async Task InterruptedAndCompletedRunsReplayToTheirExactTerminalStates()
    {
        ExecutionManifest interruptedManifest = JournalTestManifest.Create(pathToken: "-interrupted");
        ExecutionRecoveryResult interrupted = await InspectAsync(
            interruptedManifest.Fingerprint,
            Loaded(
                interruptedManifest,
                [
                    Start(interruptedManifest, 0),
                    ExecutionJournalRecord.ExecutionInterrupted(
                        TestExecutionRuns.Id,
                        interruptedManifest.Fingerprint,
                        1,
                        At(1),
                        JournalRecordReasonKind.ExecutionFault),
                ]));

        ExecutionManifest completedManifest = JournalTestManifest.Create(pathToken: "-completed");
        ExecutionRecoveryResult completed = await InspectAsync(
            completedManifest.Fingerprint,
            Loaded(
                completedManifest,
                [
                    Start(completedManifest, 0),
                    Intent(completedManifest, 1, 0),
                    ExecutionJournalRecord.MoveApplied(
                        TestExecutionRuns.Id,
                        completedManifest.Fingerprint,
                        2,
                        At(2),
                        0),
                    ExecutionJournalRecord.ExecutionCompleted(
                        TestExecutionRuns.Id,
                        completedManifest.Fingerprint,
                        3,
                        At(3)),
                ]));

        Assert.Equal(ExecutionRecoveryOutcome.Interrupted, interrupted.Outcome);
        Assert.Equal(ExecutionJournalStatus.Interrupted, interrupted.JournalState!.Status);
        Assert.Equal(ExecutionRecoveryOutcome.Completed, completed.Outcome);
        Assert.Equal(ExecutionJournalStatus.Completed, completed.JournalState!.Status);
        Assert.False(interrupted.JournalWasUpdated);
        Assert.False(completed.JournalWasUpdated);
    }

    [Fact]
    public async Task ReducerCorruptionIsMappedToManualInterventionWithoutAState()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalSnapshot invalidSnapshot = new(
            TestExecutionRuns.Id,
            manifest,
            [Start(manifest, 0), Intent(manifest, 2, 0)]);

        ExecutionRecoveryResult result = await InspectAsync(
            manifest.Fingerprint,
            new ExecutionJournalReadResult(
                ExecutionJournalReadStatus.Loaded,
                invalidSnapshot));

        Assert.Equal(ExecutionRecoveryOutcome.InvalidJournal, result.Outcome);
        Assert.Same(manifest, result.Manifest);
        Assert.Null(result.JournalState);
        ExecutionJournalReadProblem problem = Assert.Single(result.JournalProblems);
        Assert.Equal(ExecutionJournalReadProblemKind.SequenceViolation, problem.Kind);
        Assert.Equal(2, problem.RecordSequence);
    }

    [Fact]
    public async Task RepeatedReplayIsEquivalentAndProblemCollectionsStayImmutable()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        List<ExecutionJournalReadProblem> callerProblems =
        [
            new(ExecutionJournalReadProblemKind.TemporaryArtifactIgnored),
        ];
        ExecutionJournalReadResult read = new(
            ExecutionJournalReadStatus.Loaded,
            new ExecutionJournalSnapshot(
                TestExecutionRuns.Id,
                manifest,
                [Start(manifest, 0)]),
            callerProblems);
        ScriptedReplaySource source = new(read);
        ExecutionJournalRecoveryInspector inspector = new(source);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ExecutionRecoveryResult first = await inspector.InspectAsync(
            manifest.Fingerprint,
            cancellationToken);
        callerProblems.Clear();
        ExecutionRecoveryResult second = await inspector.InspectAsync(
            manifest.Fingerprint,
            cancellationToken);

        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(first.NextOperationOrdinal, second.NextOperationOrdinal);
        Assert.Equal(first.JournalState!.Status, second.JournalState!.Status);
        Assert.Equal(first.JournalState.NextSequence, second.JournalState.NextSequence);
        Assert.Equal(first.JournalProblems, second.JournalProblems);
        Assert.Equal(2, source.ReadCount);
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionJournalReadProblem>)first.JournalProblems).Clear());
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionJournalOperationState>)first.JournalState.Operations).Clear());
    }

    [Fact]
    public void ReadAndBeginContractsRejectContradictoryShapes()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalSnapshot snapshot = new(
            TestExecutionRuns.Id,
            manifest,
            [Start(manifest, 0)]);
        ExecutionJournalReadProblem blocking = new(
            ExecutionJournalReadProblemKind.ManifestMalformed);

        Assert.Throws<ArgumentException>(
            () => new ExecutionJournalReadResult(
                ExecutionJournalReadStatus.Loaded,
                snapshot,
                [blocking]));
        Assert.Throws<ArgumentException>(
            () => new ExecutionJournalReadResult(ExecutionJournalReadStatus.Invalid));
        Assert.Throws<ArgumentException>(
            () => new JournalBeginResult(
                JournalBeginStatus.InvalidExistingJournal,
                snapshot));

        List<ExecutionJournalReadProblem> callerProblems = [blocking];
        JournalBeginResult invalid = JournalBeginResult.InvalidExistingJournal(
            manifest.Fingerprint,
            callerProblems);
        callerProblems.Clear();

        Assert.Equal(JournalBeginStatus.InvalidExistingJournal, invalid.Status);
        Assert.Null(invalid.Snapshot);
        Assert.Equal(manifest.Fingerprint, invalid.ExistingFingerprint);
        Assert.Equal(blocking, Assert.Single(invalid.Problems));
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionJournalReadProblem>)invalid.Problems).Clear());

        ExecutionJournalState inProgress = ExecutionJournalStateMachine.Replay(snapshot);
        Assert.Throws<ArgumentException>(
            () => new ExecutionRecoveryResult(
                manifest.Fingerprint,
                ExecutionRecoveryOutcome.Completed,
                manifest,
                journalState: inProgress));
        Assert.Throws<ArgumentException>(
            () => new ExecutionRecoveryResult(
                manifest.Fingerprint,
                ExecutionRecoveryOutcome.InvalidJournal,
                manifest,
                problems:
                [
                    new ExecutionProblem(
                        ExecutionProblemKind.InvalidJournal,
                        "An invalid result cannot retain trusted reduced state."),
                ],
                journalState: inProgress,
                journalProblems: [blocking]));
    }

    private static async ValueTask<ExecutionRecoveryResult> InspectAsync(
        PlanFingerprint fingerprint,
        ExecutionJournalReadResult read)
    {
        ExecutionJournalRecoveryInspector inspector = new(new ScriptedReplaySource(read));
        return await inspector.InspectAsync(
            fingerprint,
            TestContext.Current.CancellationToken);
    }

    private static ExecutionJournalReadResult Loaded(
        ExecutionManifest manifest,
        IEnumerable<ExecutionJournalRecord> records) => new(
            ExecutionJournalReadStatus.Loaded,
            new ExecutionJournalSnapshot(TestExecutionRuns.Id, manifest, records));

    private static ExecutionJournalRecord Start(ExecutionManifest manifest, long sequence) =>
        ExecutionJournalRecord.ExecutionStarted(
            TestExecutionRuns.Id,
            manifest.Fingerprint,
            sequence,
            At(sequence));

    private static ExecutionJournalRecord Intent(
        ExecutionManifest manifest,
        long sequence,
        int ordinal) => ExecutionJournalRecord.MoveIntent(
            TestExecutionRuns.Id,
            manifest.Fingerprint,
            sequence,
            At(sequence),
            ordinal,
            ExpectedIdentity);

    private static DateTimeOffset At(long sequence) => StartedAt.AddSeconds(sequence);

    private sealed class ScriptedReplaySource(ExecutionJournalReadResult result)
        : IExecutionJournalReplaySource
    {
        public int ReadCount { get; private set; }

        public ValueTask<ExecutionJournalReadResult> ReadForReplayAsync(
            PlanFingerprint fingerprint,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(fingerprint);
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(result);
        }
    }
}
