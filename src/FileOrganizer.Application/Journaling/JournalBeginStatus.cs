namespace FileOrganizer.Application.Journaling;

/// <summary>The outcome of atomically claiming a journal fingerprint.</summary>
public enum JournalBeginStatus
{
    Begun,
    AlreadyExists,

    /// <summary>
    /// The store could not establish whether the genesis publication completed
    /// durably. No physical operation may be attempted.
    /// </summary>
    PersistenceFailure,

    /// <summary>
    /// A different fingerprint owns an unresolved durable execution. The
    /// returned snapshot identifies the history that must be resolved first.
    /// </summary>
    BlockedByUnresolvedExecution,

    /// <summary>
    /// An existing durable journal could not be trusted. No execution claim
    /// was created and explicit manual intervention is required.
    /// </summary>
    InvalidExistingJournal,
}
