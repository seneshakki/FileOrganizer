using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Application.Orchestration;
using FileOrganizer.Application.Tests.Execution;
using FileOrganizer.Application.Tests.Journaling;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Tests.Recovery;

public sealed class ExecutionJournalRecoveryTruthTableTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    private static readonly PhysicalEntryIdentity ExpectedIdentity =
        TestPhysicalIdentities.File("entry", "volume", "test-id-v1");

    public static TheoryData<
        TruthScenario,
        ExecutionJournalStatus,
        ExecutionRecoveryOutcome,
        int?,
        int?,
        bool,
        bool,
        bool,
        ExecutionProblemKind?,
        PhysicalKnownness> Cases => new()
        {
            {
                TruthScenario.GenesisOnly,
                ExecutionJournalStatus.InProgress,
                ExecutionRecoveryOutcome.Resumable,
                null,
                0,
                true,
                false,
                false,
                null,
                PhysicalKnownness.Known
            },
            {
                TruthScenario.IntentOnly,
                ExecutionJournalStatus.AwaitingRecovery,
                ExecutionRecoveryOutcome.RecoveryRequired,
                0,
                0,
                false,
                false,
                false,
                ExecutionProblemKind.RecoveryRequired,
                PhysicalKnownness.PendingReconciliation
            },
            {
                TruthScenario.Applied,
                ExecutionJournalStatus.InProgress,
                ExecutionRecoveryOutcome.Resumable,
                null,
                1,
                true,
                false,
                false,
                null,
                PhysicalKnownness.Known
            },
            {
                TruthScenario.NotApplied,
                ExecutionJournalStatus.Resumable,
                ExecutionRecoveryOutcome.Resumable,
                null,
                0,
                true,
                false,
                false,
                null,
                PhysicalKnownness.Known
            },
            {
                TruthScenario.Conflict,
                ExecutionJournalStatus.Conflict,
                ExecutionRecoveryOutcome.Conflict,
                null,
                0,
                false,
                true,
                true,
                ExecutionProblemKind.DestinationConflict,
                PhysicalKnownness.Known
            },
            {
                TruthScenario.Blocked,
                ExecutionJournalStatus.Blocked,
                ExecutionRecoveryOutcome.Blocked,
                null,
                0,
                false,
                true,
                true,
                ExecutionProblemKind.SourceRevalidationFailed,
                PhysicalKnownness.Known
            },
            {
                TruthScenario.Ambiguous,
                ExecutionJournalStatus.ManualRecovery,
                ExecutionRecoveryOutcome.Ambiguous,
                null,
                0,
                false,
                true,
                false,
                ExecutionProblemKind.MoveOutcomeAmbiguous,
                PhysicalKnownness.ManualResolutionRequired
            },
            {
                TruthScenario.ManualRecovery,
                ExecutionJournalStatus.ManualRecovery,
                ExecutionRecoveryOutcome.ManualRecoveryRequired,
                null,
                0,
                false,
                true,
                false,
                ExecutionProblemKind.RecoveryRequired,
                PhysicalKnownness.ManualResolutionRequired
            },
            {
                TruthScenario.MultipleAppliedThenPendingIntent,
                ExecutionJournalStatus.AwaitingRecovery,
                ExecutionRecoveryOutcome.RecoveryRequired,
                2,
                2,
                false,
                false,
                false,
                ExecutionProblemKind.RecoveryRequired,
                PhysicalKnownness.PendingReconciliation
            },
            {
                TruthScenario.AllAppliedWithoutTerminal,
                ExecutionJournalStatus.InProgress,
                ExecutionRecoveryOutcome.Resumable,
                null,
                null,
                true,
                false,
                false,
                null,
                PhysicalKnownness.Known
            },
            {
                TruthScenario.Completed,
                ExecutionJournalStatus.Completed,
                ExecutionRecoveryOutcome.Completed,
                null,
                null,
                false,
                true,
                true,
                null,
                PhysicalKnownness.Known
            },
            {
                TruthScenario.Interrupted,
                ExecutionJournalStatus.Interrupted,
                ExecutionRecoveryOutcome.Interrupted,
                null,
                1,
                false,
                true,
                true,
                ExecutionProblemKind.JournalFailure,
                PhysicalKnownness.Known
            },
        };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DurableJournalTruthTablePreservesExactRecoverySemantics(
        TruthScenario scenario,
        ExecutionJournalStatus expectedJournalStatus,
        ExecutionRecoveryOutcome expectedRecoveryOutcome,
        int? expectedActiveOrdinal,
        int? expectedNextOrdinal,
        bool expectedCanResume,
        bool expectedIsTerminal,
        bool expectedReleasesOwnership,
        ExecutionProblemKind? expectedProblemKind,
        PhysicalKnownness expectedKnownness)
    {
        ExecutionManifest manifest = ManifestFor(scenario);
        ExecutionJournalRecord[] records = RecordsFor(scenario, manifest);
        ExecutionJournalState reduced = ExecutionJournalStateMachine.Replay(
            manifest,
            records);
        ExecutionRecoveryResult recovery = await InspectAsync(
            manifest,
            records);

        Assert.Equal(expectedJournalStatus, reduced.Status);
        Assert.Equal(expectedActiveOrdinal, reduced.ActiveOperationOrdinal);
        Assert.Equal(expectedCanResume, reduced.CanResume);
        Assert.Equal(expectedIsTerminal, reduced.IsTerminal);
        Assert.Equal(expectedReleasesOwnership, reduced.ReleasesGlobalExecutionOwnership);
        Assert.Equal(ExpectedStatuses(scenario), reduced.Operations.Select(operation => operation.Status));
        Assert.All(
            reduced.Operations,
            operation => Assert.Equal(
                operation.Status == JournalOperationStatus.NotStarted
                    ? null
                    : ExpectedIdentity,
                operation.ExpectedEntryIdentity));
        AssertPhysicalKnownness(expectedKnownness, reduced);

        Assert.Equal(expectedRecoveryOutcome, recovery.Outcome);
        Assert.Equal(expectedNextOrdinal, recovery.NextOperationOrdinal);
        Assert.Same(manifest, recovery.Manifest);
        Assert.Equal(expectedJournalStatus, recovery.JournalState!.Status);
        Assert.False(recovery.JournalWasUpdated);
        if (expectedProblemKind is ExecutionProblemKind problemKind)
        {
            Assert.Equal(problemKind, Assert.Single(recovery.Problems).Kind);
        }
        else
        {
            Assert.Empty(recovery.Problems);
        }

        Assert.Empty(recovery.JournalProblems);
    }

    [Fact]
    public async Task CorruptTerminalTailMakesTheEntirePhysicalHistoryUntrusted()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalRecord[] records =
        [
            Start(manifest, 0),
            Intent(manifest, 1, 0),
            Applied(manifest, 2, 0),
            ExecutionJournalRecord.ExecutionCompleted(
                TestExecutionRuns.Id,
                manifest.Fingerprint,
                3,
                At(3)),
            ExecutionJournalRecord.ExecutionInterrupted(
                TestExecutionRuns.Id,
                manifest.Fingerprint,
                4,
                At(4),
                JournalRecordReasonKind.ExecutionFault),
        ];

        ExecutionRecoveryResult recovery = await InspectAsync(manifest, records);

        Assert.Equal(ExecutionRecoveryOutcome.InvalidJournal, recovery.Outcome);
        Assert.Same(manifest, recovery.Manifest);
        Assert.Null(recovery.JournalState);
        Assert.Null(recovery.NextOperationOrdinal);
        Assert.Equal(ExecutionProblemKind.InvalidJournal, Assert.Single(recovery.Problems).Kind);
        ExecutionJournalReadProblem journalProblem = Assert.Single(recovery.JournalProblems);
        Assert.Equal(ExecutionJournalReadProblemKind.TerminalTail, journalProblem.Kind);
        Assert.Equal(4, journalProblem.RecordSequence);
        Assert.True(journalProblem.RequiresManualIntervention);
    }

    private static ExecutionManifest ManifestFor(TruthScenario scenario) =>
        JournalTestManifest.Create(
            operationCount: scenario switch
            {
                TruthScenario.MultipleAppliedThenPendingIntent => 4,
                TruthScenario.AllAppliedWithoutTerminal
                    or TruthScenario.Completed
                    or TruthScenario.Interrupted => 3,
                _ => 2,
            },
            pathToken: $"-{scenario}-");

    private static ExecutionJournalRecord[] RecordsFor(
        TruthScenario scenario,
        ExecutionManifest manifest) => scenario switch
        {
            TruthScenario.GenesisOnly =>
            [
                Start(manifest, 0),
            ],
            TruthScenario.IntentOnly =>
            [
                Start(manifest, 0),
                Intent(manifest, 1, 0),
            ],
            TruthScenario.Applied =>
            [
                Start(manifest, 0),
                Intent(manifest, 1, 0),
                Applied(manifest, 2, 0),
            ],
            TruthScenario.NotApplied =>
            [
                Start(manifest, 0),
                Intent(manifest, 1, 0),
                ExecutionJournalRecord.MoveNotApplied(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    2,
                    At(2),
                    0,
                    JournalRecordReasonKind.MoveDidNotApply),
            ],
            TruthScenario.Conflict =>
            [
                Start(manifest, 0),
                Intent(manifest, 1, 0),
                ExecutionJournalRecord.MoveConflict(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    2,
                    At(2),
                    0,
                    JournalRecordReasonKind.DestinationConflict),
            ],
            TruthScenario.Blocked =>
            [
                Start(manifest, 0),
                Intent(manifest, 1, 0),
                ExecutionJournalRecord.MoveNotApplied(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    2,
                    At(2),
                    0,
                    JournalRecordReasonKind.SourceRevalidationFailed),
            ],
            TruthScenario.Ambiguous =>
            [
                Start(manifest, 0),
                Intent(manifest, 1, 0),
                ExecutionJournalRecord.MoveAmbiguous(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    2,
                    At(2),
                    0,
                    JournalRecordReasonKind.MoveOutcomeUnknown),
            ],
            TruthScenario.ManualRecovery =>
            [
                Start(manifest, 0),
                Intent(manifest, 1, 0),
                ExecutionJournalRecord.MoveManualRecovery(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    2,
                    At(2),
                    0,
                    JournalRecordReasonKind.RecoveryInspectionFailed),
            ],
            TruthScenario.MultipleAppliedThenPendingIntent =>
            [
                Start(manifest, 0),
                Intent(manifest, 1, 0),
                Applied(manifest, 2, 0),
                Intent(manifest, 3, 1),
                Applied(manifest, 4, 1),
                Intent(manifest, 5, 2),
            ],
            TruthScenario.AllAppliedWithoutTerminal => AllApplied(manifest),
            TruthScenario.Completed =>
            [
                .. AllApplied(manifest),
                ExecutionJournalRecord.ExecutionCompleted(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    7,
                    At(7)),
            ],
            TruthScenario.Interrupted =>
            [
                Start(manifest, 0),
                Intent(manifest, 1, 0),
                Applied(manifest, 2, 0),
                ExecutionJournalRecord.ExecutionInterrupted(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    3,
                    At(3),
                    JournalRecordReasonKind.ExecutionFault),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private static ExecutionJournalRecord[] AllApplied(ExecutionManifest manifest) =>
    [
        Start(manifest, 0),
        Intent(manifest, 1, 0),
        Applied(manifest, 2, 0),
        Intent(manifest, 3, 1),
        Applied(manifest, 4, 1),
        Intent(manifest, 5, 2),
        Applied(manifest, 6, 2),
    ];

    private static JournalOperationStatus[] ExpectedStatuses(TruthScenario scenario) =>
        scenario switch
        {
            TruthScenario.GenesisOnly =>
                [JournalOperationStatus.NotStarted, JournalOperationStatus.NotStarted],
            TruthScenario.IntentOnly =>
                [JournalOperationStatus.IntentPublished, JournalOperationStatus.NotStarted],
            TruthScenario.Applied =>
                [JournalOperationStatus.Applied, JournalOperationStatus.NotStarted],
            TruthScenario.NotApplied =>
                [JournalOperationStatus.NotApplied, JournalOperationStatus.NotStarted],
            TruthScenario.Conflict =>
                [JournalOperationStatus.Conflict, JournalOperationStatus.NotStarted],
            TruthScenario.Blocked =>
                [JournalOperationStatus.Blocked, JournalOperationStatus.NotStarted],
            TruthScenario.Ambiguous =>
                [JournalOperationStatus.Ambiguous, JournalOperationStatus.NotStarted],
            TruthScenario.ManualRecovery =>
                [JournalOperationStatus.ManualRecovery, JournalOperationStatus.NotStarted],
            TruthScenario.MultipleAppliedThenPendingIntent =>
            [
                JournalOperationStatus.Applied,
                JournalOperationStatus.Applied,
                JournalOperationStatus.IntentPublished,
                JournalOperationStatus.NotStarted,
            ],
            TruthScenario.AllAppliedWithoutTerminal or TruthScenario.Completed =>
            [
                JournalOperationStatus.Applied,
                JournalOperationStatus.Applied,
                JournalOperationStatus.Applied,
            ],
            TruthScenario.Interrupted =>
            [
                JournalOperationStatus.Applied,
                JournalOperationStatus.NotStarted,
                JournalOperationStatus.NotStarted,
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private static void AssertPhysicalKnownness(
        PhysicalKnownness knownness,
        ExecutionJournalState state)
    {
        JournalOperationStatus[] unknownStatuses = knownness switch
        {
            PhysicalKnownness.Known => [],
            PhysicalKnownness.PendingReconciliation => [JournalOperationStatus.IntentPublished],
            PhysicalKnownness.ManualResolutionRequired =>
            [
                JournalOperationStatus.Ambiguous,
                JournalOperationStatus.ManualRecovery,
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(knownness)),
        };
        ExecutionJournalOperationState[] unknownOperations = state.Operations
            .Where(operation => unknownStatuses.Contains(operation.Status))
            .ToArray();

        if (knownness == PhysicalKnownness.Known)
        {
            Assert.DoesNotContain(
                state.Operations,
                operation => operation.Status is
                    JournalOperationStatus.IntentPublished
                    or JournalOperationStatus.Ambiguous
                    or JournalOperationStatus.ManualRecovery);
            return;
        }

        Assert.Single(unknownOperations);
    }

    private static async ValueTask<ExecutionRecoveryResult> InspectAsync(
        ExecutionManifest manifest,
        IEnumerable<ExecutionJournalRecord> records)
    {
        ExecutionJournalReadResult read = new(
            ExecutionJournalReadStatus.Loaded,
            new ExecutionJournalSnapshot(TestExecutionRuns.Id, manifest, records));
        ExecutionJournalRecoveryInspector inspector = new(new ReplaySource(read));
        return await inspector.InspectAsync(
            manifest.Fingerprint,
            TestContext.Current.CancellationToken);
    }

    private static ExecutionJournalRecord Start(
        ExecutionManifest manifest,
        long sequence) => ExecutionJournalRecord.ExecutionStarted(
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

    private static ExecutionJournalRecord Applied(
        ExecutionManifest manifest,
        long sequence,
        int ordinal) => ExecutionJournalRecord.MoveApplied(
            TestExecutionRuns.Id,
            manifest.Fingerprint,
            sequence,
            At(sequence),
            ordinal);

    private static DateTimeOffset At(long sequence) => StartedAt.AddSeconds(sequence);

    public enum TruthScenario
    {
        GenesisOnly,
        IntentOnly,
        Applied,
        NotApplied,
        Conflict,
        Blocked,
        Ambiguous,
        ManualRecovery,
        MultipleAppliedThenPendingIntent,
        AllAppliedWithoutTerminal,
        Completed,
        Interrupted,
    }

    public enum PhysicalKnownness
    {
        Known,
        PendingReconciliation,
        ManualResolutionRequired,
    }

    private sealed class ReplaySource(ExecutionJournalReadResult read)
        : IExecutionJournalReplaySource
    {
        public ValueTask<ExecutionJournalReadResult> ReadForReplayAsync(
            PlanFingerprint fingerprint,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(fingerprint);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(read);
        }
    }
}
