using FileOrganizer.Application.Execution;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Journaling;

/// <summary>Validates and restores persisted logical journal values without I/O.</summary>
public static class ExecutionJournalPersistence
{
    public static ExecutionJournalRecord RestoreRecord(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc,
        ExecutionJournalRecordKind kind,
        int? operationOrdinal,
        PhysicalEntryIdentity? expectedEntryIdentity,
        JournalRecordReasonKind reason) =>
        ExecutionJournalRecord.Restore(
            runId,
            fingerprint,
            sequence,
            recordedAtUtc,
            kind,
            operationOrdinal,
            expectedEntryIdentity,
            reason);
}
