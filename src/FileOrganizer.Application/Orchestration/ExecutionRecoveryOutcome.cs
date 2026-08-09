namespace FileOrganizer.Application.Orchestration;

/// <summary>The explicit result of read-only execution recovery.</summary>
public enum ExecutionRecoveryOutcome
{
    NoJournal,
    Busy,
    Completed,
    Resumable,
    Blocked,
    Conflict,
    Ambiguous,
    ManualRecoveryRequired,
    Interrupted,
    InvalidJournal,
}
