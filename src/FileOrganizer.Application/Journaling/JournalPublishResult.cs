namespace FileOrganizer.Application.Journaling;

/// <summary>
/// Reports whether an append was published, already existed as the same
/// logical record at the requested sequence, or collided with a different record.
/// </summary>
public sealed class JournalPublishResult
{
    private JournalPublishResult(
        JournalPublishStatus status,
        ExecutionJournalRecord? existingRecord)
    {
        Status = status;
        ExistingRecord = existingRecord;
    }

    public JournalPublishStatus Status { get; }

    public ExecutionJournalRecord? ExistingRecord { get; }

    public static JournalPublishResult Published() =>
        new(JournalPublishStatus.Published, null);

    public static JournalPublishResult AlreadyPublished(
        ExecutionJournalRecord existingRecord)
    {
        ArgumentNullException.ThrowIfNull(existingRecord);
        return new(JournalPublishStatus.AlreadyPublished, existingRecord);
    }

    public static JournalPublishResult SequenceConflict(
        ExecutionJournalRecord? existingRecord = null)
    {
        return new(JournalPublishStatus.SequenceConflict, existingRecord);
    }
}
