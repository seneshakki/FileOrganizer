namespace FileOrganizer.Application.Journaling;

/// <summary>Identifies why a durable journal stream cannot be trusted.</summary>
public enum JournalSequenceProblemKind
{
    InvalidManifestOrdinals,
    SequenceGap,
    FingerprintMismatch,
    RunMismatch,
    StartRequired,
    DuplicateStart,
    RecordAfterTerminal,
    UnexpectedOperationOrdinal,
    OperationIntentRequired,
    OperationAlreadyActive,
    CompletionBeforeAllApplied,
    InterruptionDuringPendingIntent,
    ResumeRequired,
    ResumeNotAllowed,
}
