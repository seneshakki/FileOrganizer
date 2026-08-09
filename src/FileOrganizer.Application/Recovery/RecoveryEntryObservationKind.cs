namespace FileOrganizer.Application.Recovery;

/// <summary>The observable state of a source or target path during recovery.</summary>
public enum RecoveryEntryObservationKind
{
    Missing,
    Present,
    InspectionFailed,
}
