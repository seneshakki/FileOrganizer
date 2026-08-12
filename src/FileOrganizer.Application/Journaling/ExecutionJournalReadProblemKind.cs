namespace FileOrganizer.Application.Journaling;

/// <summary>
/// Stable, persistence-neutral categories reported while reading a durable journal.
/// Adapter paths and platform exception text are deliberately excluded.
/// </summary>
public enum ExecutionJournalReadProblemKind
{
    ManifestMissing,
    ManifestMalformed,
    UnsupportedFormat,
    FingerprintOwnershipMismatch,
    RecordMalformed,
    SequenceViolation,
    PayloadIntegrityViolation,
    HashChainViolation,
    RunOwnershipMismatch,
    /// <summary>More than one durable execution claims unresolved global ownership.</summary>
    ConflictingExecutionOwnership,
    TerminalTail,
    UnexpectedStorageEntry,
    TemporaryArtifactIgnored,
    UnsafeStorage,
    StorageReadFailure,
}
