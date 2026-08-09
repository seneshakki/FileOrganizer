namespace FileOrganizer.Application.Recovery;

/// <summary>Identifies why recovery could not safely observe a path.</summary>
public enum RecoveryInspectionFailureKind
{
    AccessDenied,
    InvalidPath,
    UnsupportedEntry,
    EntryChanged,
    IoFailure,
}
