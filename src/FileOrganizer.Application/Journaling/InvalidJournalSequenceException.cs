namespace FileOrganizer.Application.Journaling;

/// <summary>Reports a structurally unsafe or contradictory journal stream.</summary>
public sealed class InvalidJournalSequenceException : InvalidOperationException
{
    internal InvalidJournalSequenceException(
        JournalSequenceProblemKind problemKind,
        int recordIndex,
        long? recordSequence,
        string message)
        : base(message)
    {
        ProblemKind = problemKind;
        RecordIndex = recordIndex;
        RecordSequence = recordSequence;
    }

    public JournalSequenceProblemKind ProblemKind { get; }

    public int RecordIndex { get; }

    public long? RecordSequence { get; }
}
