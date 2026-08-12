namespace FileOrganizer.Application.Journaling;

/// <summary>An immutable durable-read diagnostic without storage-path information.</summary>
public sealed record ExecutionJournalReadProblem
{
    public ExecutionJournalReadProblem(
        ExecutionJournalReadProblemKind kind,
        long? recordSequence = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (recordSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recordSequence));
        }

        Kind = kind;
        RecordSequence = recordSequence;
    }

    public ExecutionJournalReadProblemKind Kind { get; }

    public long? RecordSequence { get; }

    public bool RequiresManualIntervention =>
        Kind != ExecutionJournalReadProblemKind.TemporaryArtifactIgnored;
}
