namespace FileOrganizer.Application.Journaling;

/// <summary>
/// Returns the exact durable journal created, found by fingerprint, or found
/// blocking creation because its execution is still unresolved.
/// </summary>
public sealed class JournalBeginResult
{
    public JournalBeginResult(
        JournalBeginStatus status,
        ExecutionJournalSnapshot snapshot)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentNullException.ThrowIfNull(snapshot);
        Status = status;
        Snapshot = snapshot;
    }

    public JournalBeginStatus Status { get; }

    public ExecutionJournalSnapshot Snapshot { get; }
}
