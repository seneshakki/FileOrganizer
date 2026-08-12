namespace FileOrganizer.Infrastructure.Journaling;

/// <summary>Identifies why durable journal bytes cannot be trusted.</summary>
public enum JournalLoadProblemKind
{
    MissingManifest,
    MalformedManifest,
    UnsupportedJournalVersion,
    WrongFingerprint,
    MalformedRecord,
    DuplicateSequence,
    SequenceGap,
    RecordFileNameMismatch,
    PayloadHashMismatch,
    PreviousHashMismatch,
    WrongRunOwnership,
    RecordAfterTerminal,
    TemporaryFilePresent,
    UnexpectedDurableEntry,
    UnsafeJournalPath,
    IoFailure,
}
