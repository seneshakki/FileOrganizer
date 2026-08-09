namespace FileOrganizer.Application.Journaling;

/// <summary>The latest durable state of one manifest move.</summary>
public enum JournalOperationStatus
{
    NotStarted,
    IntentPublished,
    Applied,
    NotApplied,
    Blocked,
    Conflict,
    Ambiguous,
    ManualRecovery,
}
