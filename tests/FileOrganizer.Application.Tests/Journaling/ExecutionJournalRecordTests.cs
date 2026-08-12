using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Tests.Execution;
using FileOrganizer.Core.Physical;
using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Application.Tests.Journaling;

public sealed class ExecutionJournalRecordTests
{
    private static readonly ExecutionRunId RunId =
        new("0123456789abcdef0123456789abcdef");

    private static readonly DateTimeOffset RecordedAt =
        new(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void NormalizesTimestampAndUsesStructuralEquality()
    {
        PlanFingerprint fingerprint = JournalTestManifest.Create().Fingerprint;
        PhysicalEntryIdentity identity = TestPhysicalIdentities.File(
            "entry",
            volumeIdentity: "volume",
            scheme: "test-id-v1");
        ExecutionJournalRecord first = ExecutionJournalRecord.MoveIntent(
            RunId,
            fingerprint,
            1,
            RecordedAt,
            0,
            identity);
        ExecutionJournalRecord second = ExecutionJournalRecord.MoveIntent(
            new ExecutionRunId(RunId.Value),
            new PlanFingerprint(fingerprint.Value),
            1,
            RecordedAt.ToUniversalTime(),
            0,
            TestPhysicalIdentities.File(
                "entry",
                volumeIdentity: "volume",
                scheme: "test-id-v1"));

        Assert.Equal(RecordedAt.ToUniversalTime(), first.RecordedAtUtc);
        Assert.Same(identity, first.ExpectedEntryIdentity);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void MoveIntentRejectsADirectoryIdentity()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();

        Assert.Throws<ArgumentException>(
            () => ExecutionJournalRecord.MoveIntent(
                RunId,
                manifest.Fingerprint,
                1,
                RecordedAt,
                0,
                TestPhysicalIdentities.Directory("not-a-file")));
    }

    [Fact]
    public void RejectsInvalidSequenceAndReasonShapes()
    {
        PlanFingerprint fingerprint = JournalTestManifest.Create().Fingerprint;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExecutionJournalRecord.ExecutionStarted(RunId, fingerprint, -1, RecordedAt));
        Assert.Throws<ArgumentException>(
            () => ExecutionJournalRecord.MoveNotApplied(
                RunId,
                fingerprint,
                1,
                RecordedAt,
                0,
                JournalRecordReasonKind.None));
        Assert.Throws<ArgumentException>(
            () => ExecutionJournalRecord.ExecutionStopped(
                RunId,
                fingerprint,
                1,
                RecordedAt,
                0,
                JournalRecordReasonKind.ExecutionFault));
        Assert.Throws<ArgumentException>(
            () => ExecutionJournalRecord.MoveApplied(
                RunId,
                fingerprint,
                1,
                RecordedAt,
                0,
                JournalRecordReasonKind.DestinationConflict));
    }

    [Fact]
    public void SnapshotRetainsTheCompleteManifestAndCopiesCallerRecords()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        List<ExecutionJournalRecord> records =
        [
            ExecutionJournalRecord.ExecutionStarted(RunId, manifest.Fingerprint, 0, RecordedAt),
        ];

        ExecutionJournalSnapshot snapshot = new(RunId, manifest, records);
        records.Clear();

        Assert.Same(manifest, snapshot.Manifest);
        Assert.Single(snapshot.Records);
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionJournalRecord>)snapshot.Records).Clear());
    }
}
