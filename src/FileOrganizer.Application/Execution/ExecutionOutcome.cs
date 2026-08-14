namespace FileOrganizer.Application.Execution;

/// <summary>The explicit terminal outcome of one orchestration attempt.</summary>
public enum ExecutionOutcome
{
    /// <summary>The eligible manifest contained no move operations.</summary>
    NoWork,

    /// <summary>The plan was rejected before an execution manifest existed.</summary>
    Rejected,

    /// <summary>Every manifest operation was durably recorded as applied.</summary>
    Completed,

    /// <summary>No operation was applied and execution stopped on a definite failure.</summary>
    Stopped,

    /// <summary>At least one operation was applied before fail-stop behavior halted the run.</summary>
    PartiallyCompleted,

    /// <summary>Cancellation stopped the run at a safe orchestration boundary.</summary>
    Interrupted,

    /// <summary>The durable state requires explicit recovery or manual reconciliation.</summary>
    RecoveryRequired,
}
