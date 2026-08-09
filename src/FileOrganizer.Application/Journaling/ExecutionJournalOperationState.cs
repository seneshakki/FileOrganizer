using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Journaling;

/// <summary>The immutable reduced journal state of one manifest operation.</summary>
public sealed class ExecutionJournalOperationState
{
    internal ExecutionJournalOperationState(
        int ordinal,
        JournalOperationStatus status,
        long? lastRecordSequence,
        JournalRecordReasonKind reason,
        EntryIdentity? expectedEntryIdentity)
    {
        Ordinal = ordinal;
        Status = status;
        LastRecordSequence = lastRecordSequence;
        Reason = reason;
        ExpectedEntryIdentity = expectedEntryIdentity;
    }

    public int Ordinal { get; }

    public JournalOperationStatus Status { get; }

    public long? LastRecordSequence { get; }

    public JournalRecordReasonKind Reason { get; }

    /// <summary>
    /// Gets the identity durably captured by the latest intent for this move.
    /// It remains available after resolution so recovery replay is repeatable.
    /// </summary>
    public EntryIdentity? ExpectedEntryIdentity { get; }
}
