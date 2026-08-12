namespace FileOrganizer.Application.Orchestration;

/// <summary>The explicit result of read-only execution recovery.</summary>
public enum ExecutionRecoveryOutcome
{
    NoJournal,
    Busy,
    Completed,
    Resumable,
    /// <summary>
    /// A durable move intent has no durable outcome. Physical reconciliation is
    /// required, but this classification never performs it.
    /// </summary>
    RecoveryRequired,
    Blocked,
    Conflict,
    Ambiguous,
    ManualRecoveryRequired,
    Interrupted,
    InvalidJournal,
}
