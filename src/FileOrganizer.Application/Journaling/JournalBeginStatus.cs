namespace FileOrganizer.Application.Journaling;

/// <summary>The outcome of atomically claiming a journal fingerprint.</summary>
public enum JournalBeginStatus
{
    Begun,
    AlreadyExists,
    /// <summary>
    /// A different fingerprint owns an unresolved durable execution. The
    /// returned snapshot identifies the history that must be resolved first.
    /// </summary>
    BlockedByUnresolvedExecution,
}
