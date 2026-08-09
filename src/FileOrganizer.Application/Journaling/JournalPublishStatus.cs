namespace FileOrganizer.Application.Journaling;

/// <summary>The outcome of an atomic durable journal publication.</summary>
public enum JournalPublishStatus
{
    Published,
    AlreadyPublished,
    SequenceConflict,
}
