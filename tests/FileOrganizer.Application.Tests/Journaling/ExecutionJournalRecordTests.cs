using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Application.Tests.Journaling;

public sealed class ExecutionJournalRecordTests
{
    private static readonly DateTimeOffset RecordedAt =
        new(2026, 8, 9, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void NormalizesTimestampAndUsesStructuralEquality()
    {
        PlanFingerprint fingerprint = JournalTestManifest.Create().Fingerprint;
        EntryIdentity identity = new("test-id-v1", "volume", "entry");
        ExecutionJournalRecord first = ExecutionJournalRecord.MoveIntent(
            fingerprint,
            1,
            RecordedAt,
            0,
            identity);
        ExecutionJournalRecord second = ExecutionJournalRecord.MoveIntent(
            new PlanFingerprint(fingerprint.Value.ToUpperInvariant()),
            1,
            RecordedAt.ToUniversalTime(),
            0,
            new EntryIdentity("test-id-v1", "volume", "entry"));

        Assert.Equal(RecordedAt.ToUniversalTime(), first.RecordedAtUtc);
        Assert.Same(identity, first.ExpectedEntryIdentity);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void RejectsInvalidSequenceAndReasonShapes()
    {
        PlanFingerprint fingerprint = JournalTestManifest.Create().Fingerprint;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExecutionJournalRecord.ExecutionStarted(fingerprint, -1, RecordedAt));
        Assert.Throws<ArgumentException>(
            () => ExecutionJournalRecord.MoveNotApplied(
                fingerprint,
                1,
                RecordedAt,
                0,
                JournalRecordReasonKind.None));
        Assert.Throws<ArgumentException>(
            () => ExecutionJournalRecord.ExecutionStopped(
                fingerprint,
                1,
                RecordedAt,
                0,
                JournalRecordReasonKind.ExecutionFault));
        Assert.Throws<ArgumentException>(
            () => ExecutionJournalRecord.MoveApplied(
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
            ExecutionJournalRecord.ExecutionStarted(manifest.Fingerprint, 0, RecordedAt),
        ];

        ExecutionJournalSnapshot snapshot = new(manifest, records);
        records.Clear();

        Assert.Same(manifest, snapshot.Manifest);
        Assert.Single(snapshot.Records);
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionJournalRecord>)snapshot.Records).Clear());
    }
}
