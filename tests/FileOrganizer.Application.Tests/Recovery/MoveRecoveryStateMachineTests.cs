using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Application.Recovery;
using FileOrganizer.Application.Tests.Execution;
using FileOrganizer.Application.Tests.Journaling;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Tests.Recovery;

public sealed class MoveRecoveryStateMachineTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);

    private static readonly PhysicalEntryIdentity Expected =
        TestPhysicalIdentities.File("entry-42", "volume-a", "windows-file-id-v1");

    private static readonly PhysicalEntryIdentity Different =
        TestPhysicalIdentities.File("entry-99", "volume-a", "windows-file-id-v1");

    public static TheoryData<
        RecoveryEntryObservation,
        RecoveryEntryObservation,
        RecoveryDisposition,
        RecoveryReasonKind> Cases => new()
        {
            {
                RecoveryEntryObservation.Missing(),
                RecoveryEntryObservation.Present(Expected),
                RecoveryDisposition.Applied,
                RecoveryReasonKind.ExpectedEntryAtTargetOnly
            },
            {
                RecoveryEntryObservation.Present(Expected),
                RecoveryEntryObservation.Missing(),
                RecoveryDisposition.NotApplied,
                RecoveryReasonKind.ExpectedEntryAtSourceOnly
            },
            {
                RecoveryEntryObservation.Present(Different),
                RecoveryEntryObservation.Missing(),
                RecoveryDisposition.Ambiguous,
                RecoveryReasonKind.SourceIdentityChanged
            },
            {
                RecoveryEntryObservation.Present(Different),
                RecoveryEntryObservation.Present(Expected),
                RecoveryDisposition.Applied,
                RecoveryReasonKind.ExpectedEntryAtTargetOnly
            },
            {
                RecoveryEntryObservation.Present(Expected),
                RecoveryEntryObservation.Present(Different),
                RecoveryDisposition.Conflict,
                RecoveryReasonKind.TargetOccupiedByDifferentEntry
            },
            {
                RecoveryEntryObservation.Present(Expected),
                RecoveryEntryObservation.Present(Expected),
                RecoveryDisposition.Ambiguous,
                RecoveryReasonKind.ExpectedEntryAtBothPaths
            },
            {
                RecoveryEntryObservation.Missing(),
                RecoveryEntryObservation.Missing(),
                RecoveryDisposition.Ambiguous,
                RecoveryReasonKind.BothPathsMissing
            },
            {
                RecoveryEntryObservation.InspectionFailed(
                    RecoveryInspectionFailureKind.AccessDenied),
                RecoveryEntryObservation.Missing(),
                RecoveryDisposition.Ambiguous,
                RecoveryReasonKind.InspectionFailed
            },
            {
                RecoveryEntryObservation.Missing(),
                RecoveryEntryObservation.Present(Different),
                RecoveryDisposition.Ambiguous,
                RecoveryReasonKind.TargetOccupiedByDifferentEntry
            },
            {
                RecoveryEntryObservation.Present(Different),
                RecoveryEntryObservation.Present(
                    TestPhysicalIdentities.File(
                        "entry-100",
                        "volume-a",
                        "windows-file-id-v1")),
                RecoveryDisposition.Ambiguous,
                RecoveryReasonKind.SourceIdentityChanged
            },
        };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ClassifiesTheRecoveryTruthTablePrecisely(
        RecoveryEntryObservation source,
        RecoveryEntryObservation target,
        RecoveryDisposition disposition,
        RecoveryReasonKind reason)
    {
        RecoveryResolution resolution = MoveRecoveryStateMachine.Classify(
            Expected,
            source,
            target);

        Assert.Equal(disposition, resolution.Disposition);
        Assert.Equal(reason, resolution.Reason);
    }

    [Theory]
    [InlineData(RecoveryDisposition.Applied, ExecutionJournalRecordKind.MoveApplied, JournalRecordReasonKind.RecoveryConfirmedApplied)]
    [InlineData(RecoveryDisposition.NotApplied, ExecutionJournalRecordKind.MoveNotApplied, JournalRecordReasonKind.RecoveryConfirmedNotApplied)]
    [InlineData(RecoveryDisposition.Conflict, ExecutionJournalRecordKind.MoveConflict, JournalRecordReasonKind.RecoveryTargetIdentityChanged)]
    [InlineData(RecoveryDisposition.Ambiguous, ExecutionJournalRecordKind.MoveAmbiguous, JournalRecordReasonKind.RecoveryBothPathsMissing)]
    public void CreatesTheExactNextJournalPublication(
        RecoveryDisposition disposition,
        ExecutionJournalRecordKind recordKind,
        JournalRecordReasonKind journalReason)
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalState pending = Pending(manifest);
        (RecoveryEntryObservation source, RecoveryEntryObservation target) =
            ObservationsFor(disposition);

        RecoveryDecision decision = MoveRecoveryStateMachine.Decide(
            TestExecutionRuns.Id,
            pending,
            source,
            target,
            StartedAt.AddMinutes(1));

        Assert.Equal(RecoveryPublicationStatus.PublicationRequired, decision.PublicationStatus);
        ExecutionJournalRecord record = Assert.IsType<ExecutionJournalRecord>(decision.Record);
        Assert.Equal(recordKind, record.Kind);
        Assert.Equal(journalReason, record.Reason);
        Assert.Equal(2, record.Sequence);
        Assert.Equal(0, record.OperationOrdinal);
    }

    [Fact]
    public void InspectionFailureIsDurablyRecordedAsAmbiguous()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();

        RecoveryDecision decision = MoveRecoveryStateMachine.Decide(
            TestExecutionRuns.Id,
            Pending(manifest),
            RecoveryEntryObservation.InspectionFailed(
                RecoveryInspectionFailureKind.IoFailure),
            RecoveryEntryObservation.Missing(),
            StartedAt.AddMinutes(1));

        Assert.Equal(RecoveryDisposition.Ambiguous, decision.Resolution.Disposition);
        ExecutionJournalRecord record = Assert.IsType<ExecutionJournalRecord>(decision.Record);
        Assert.Equal(ExecutionJournalRecordKind.MoveAmbiguous, record.Kind);
        Assert.Equal(JournalRecordReasonKind.RecoveryInspectionFailed, record.Reason);
    }

    [Fact]
    public void RepeatingRecoveryAfterPublicationIsIdempotent()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalState pending = Pending(manifest);
        RecoveryEntryObservation missing = RecoveryEntryObservation.Missing();
        RecoveryEntryObservation found = RecoveryEntryObservation.Present(Expected);
        RecoveryDecision first = MoveRecoveryStateMachine.Decide(
            TestExecutionRuns.Id,
            pending,
            missing,
            found,
            StartedAt.AddMinutes(1));
        ExecutionJournalRecord publication = Assert.IsType<ExecutionJournalRecord>(first.Record);
        ExecutionJournalState replayed = ExecutionJournalStateMachine.Replay(
            manifest,
            [
                ExecutionJournalRecord.ExecutionStarted(TestExecutionRuns.Id, manifest.Fingerprint, 0, StartedAt),
                ExecutionJournalRecord.MoveIntent(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    1,
                    StartedAt.AddSeconds(1),
                    0,
                    Expected),
                publication,
            ]);

        RecoveryDecision repeated = MoveRecoveryStateMachine.Decide(
            TestExecutionRuns.Id,
            replayed,
            missing,
            found,
            StartedAt.AddMinutes(2));

        Assert.Equal(RecoveryPublicationStatus.AlreadyRecorded, repeated.PublicationStatus);
        Assert.Null(repeated.Record);
        Assert.Equal(RecoveryDisposition.Applied, repeated.Resolution.Disposition);
        Assert.Equal(ExecutionJournalStatus.Resumable, replayed.Status);
        Assert.True(replayed.CanResume);
    }

    [Fact]
    public void ConfirmedNotAppliedBecomesExplicitlyResumableButDoesNotResumeItself()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        RecoveryDecision decision = MoveRecoveryStateMachine.Decide(
            TestExecutionRuns.Id,
            Pending(manifest),
            RecoveryEntryObservation.Present(Expected),
            RecoveryEntryObservation.Missing(),
            StartedAt.AddMinutes(1));
        ExecutionJournalState reconciled = ExecutionJournalStateMachine.Replay(
            manifest,
            [
                ExecutionJournalRecord.ExecutionStarted(TestExecutionRuns.Id, manifest.Fingerprint, 0, StartedAt),
                ExecutionJournalRecord.MoveIntent(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    1,
                    StartedAt.AddSeconds(1),
                    0,
                    Expected),
                Assert.IsType<ExecutionJournalRecord>(decision.Record),
            ]);

        Assert.Equal(ExecutionJournalStatus.Resumable, reconciled.Status);
        Assert.True(reconciled.CanResume);
        Assert.False(reconciled.IsTerminal);
        Assert.Equal(
            JournalOperationStatus.NotApplied,
            Assert.Single(reconciled.Operations).Status);
    }

    [Fact]
    public void IdentityComparisonIsOpaqueAndOrdinal()
    {
        PhysicalEntryIdentity first = TestPhysicalIdentities.File(
            "Entry",
            "Volume",
            "Scheme");
        PhysicalEntryIdentity same = TestPhysicalIdentities.File(
            "Entry",
            "Volume",
            "Scheme");
        PhysicalEntryIdentity differentCase = TestPhysicalIdentities.File(
            "Entry",
            "Volume",
            "scheme");

        Assert.Equal(first, same);
        Assert.NotEqual(first, differentCase);
    }

    private static ExecutionJournalState Pending(ExecutionManifest manifest) =>
        ExecutionJournalStateMachine.Replay(
            manifest,
            [
                ExecutionJournalRecord.ExecutionStarted(TestExecutionRuns.Id, manifest.Fingerprint, 0, StartedAt),
                ExecutionJournalRecord.MoveIntent(
                    TestExecutionRuns.Id,
                    manifest.Fingerprint,
                    1,
                    StartedAt.AddSeconds(1),
                    0,
                    Expected),
            ]);

    private static (
        RecoveryEntryObservation Source,
        RecoveryEntryObservation Target) ObservationsFor(RecoveryDisposition disposition) =>
        disposition switch
        {
            RecoveryDisposition.Applied => (
                RecoveryEntryObservation.Missing(),
                RecoveryEntryObservation.Present(Expected)),
            RecoveryDisposition.NotApplied => (
                RecoveryEntryObservation.Present(Expected),
                RecoveryEntryObservation.Missing()),
            RecoveryDisposition.Conflict => (
                RecoveryEntryObservation.Present(Expected),
                RecoveryEntryObservation.Present(Different)),
            RecoveryDisposition.Ambiguous => (
                RecoveryEntryObservation.Missing(),
                RecoveryEntryObservation.Missing()),
            RecoveryDisposition.ManualRecovery => (
                RecoveryEntryObservation.InspectionFailed(
                    RecoveryInspectionFailureKind.IoFailure),
                RecoveryEntryObservation.Missing()),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
        };
}
