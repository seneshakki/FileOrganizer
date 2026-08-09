namespace FileOrganizer.Application.Journaling;

/// <summary>Identifies one durable logical publication in an execution journal.</summary>
public enum ExecutionJournalRecordKind
{
    ExecutionStarted,
    MoveIntent,
    MoveApplied,
    MoveNotApplied,
    MoveConflict,
    MoveAmbiguous,
    MoveManualRecovery,
    ExecutionStopped,
    ExecutionInterrupted,
    ExecutionResumed,
    ExecutionCompleted,
}
