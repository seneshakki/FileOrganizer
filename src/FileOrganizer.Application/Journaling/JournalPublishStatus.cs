namespace FileOrganizer.Application.Journaling;

/// <summary>The outcome of an atomic durable journal publication.</summary>
public enum JournalPublishStatus
{
    Published,
    AlreadyPublished,
    SequenceConflict,

    /// <summary>The durable append failed or its durability could not be established.</summary>
    PersistenceFailure,

    /// <summary>The store found journal state that cannot be trusted or safely appended.</summary>
    InvalidJournal,
}
