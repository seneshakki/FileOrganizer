namespace FileOrganizer.Application.Journaling;

/// <summary>The state derived from a complete, valid journal record stream.</summary>
public enum ExecutionJournalStatus
{
    NotStarted,
    InProgress,
    AwaitingRecovery,
    Resumable,
    Interrupted,
    Blocked,
    Conflict,
    ManualRecovery,
    Completed,
}
