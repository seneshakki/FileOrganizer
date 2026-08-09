namespace FileOrganizer.Application.Execution;

/// <summary>A stable category for orchestration, journal, and recovery failures.</summary>
public enum ExecutionProblemKind
{
    /// <summary>Manifest compilation rejected the dry-run plan.</summary>
    ManifestRejected,

    /// <summary>An exclusive execution lease could not be acquired.</summary>
    ExecutionAlreadyInProgress,

    /// <summary>The execution journal could not be read or durably updated.</summary>
    JournalFailure,

    /// <summary>The durable journal contains an invalid state transition or corrupt sequence.</summary>
    InvalidJournal,

    /// <summary>The source no longer satisfies its immutable plan precondition.</summary>
    SourceRevalidationFailed,

    /// <summary>The destination directory or target could not be revalidated safely.</summary>
    DestinationRevalidationFailed,

    /// <summary>A definite destination collision prevents the move.</summary>
    DestinationConflict,

    /// <summary>The scripted or future physical move reported a definite failure.</summary>
    MoveFailed,

    /// <summary>The operation's applied/not-applied state is ambiguous.</summary>
    MoveOutcomeAmbiguous,

    /// <summary>Cancellation was observed at a safe boundary.</summary>
    CancellationRequested,

    /// <summary>The durable state requires explicit recovery before execution can continue.</summary>
    RecoveryRequired,
}
