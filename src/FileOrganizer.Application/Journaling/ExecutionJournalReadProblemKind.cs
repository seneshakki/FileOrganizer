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
    TerminalTail,
    UnexpectedStorageEntry,
    TemporaryArtifactIgnored,
    UnsafeStorage,
    StorageReadFailure,
}
