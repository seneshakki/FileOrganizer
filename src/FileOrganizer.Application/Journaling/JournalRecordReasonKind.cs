namespace FileOrganizer.Application.Journaling;

/// <summary>Provides a stable, structured reason for a journal publication.</summary>
public enum JournalRecordReasonKind
{
    None,
    CancellationRequested,
    ExecutionFault,
    SourceRevalidationFailed,
    DestinationRevalidationFailed,
    MoveDidNotApply,
    MoveOutcomeUnknown,
    DestinationConflict,
    RecoveryConfirmedApplied,
    RecoveryConfirmedNotApplied,
    RecoverySourceIdentityChanged,
    RecoveryTargetIdentityChanged,
    RecoveryBothPathsContainExpectedEntry,
    RecoveryBothPathsMissing,
    RecoveryInspectionFailed,
}
