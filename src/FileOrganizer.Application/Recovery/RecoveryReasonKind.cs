namespace FileOrganizer.Application.Recovery;

/// <summary>Explains an exact recovery classification without relying on text.</summary>
public enum RecoveryReasonKind
{
    ExpectedEntryAtTargetOnly,
    ExpectedEntryAtSourceOnly,
    SourceIdentityChanged,
    TargetOccupiedByDifferentEntry,
    ExpectedEntryAtBothPaths,
    BothPathsMissing,
    InspectionFailed,
}
