namespace FileOrganizer.Application.Watching;

/// <summary>A stable category for watch-pipeline diagnostics.</summary>
public enum WatchDiagnosticKind
{
    BufferOverflow = 0,
    SourceInaccessible,
    SourceUnavailable,
    UnsupportedCapability,
    InputOutputFailure,
    InvalidPath,
    OutsideRoot,
    Missing,
    Inaccessible,
    UnsupportedEntry,
    ReparsePoint,
    Unstable,
    IncompleteBaseline,
    BaselineMismatch,
    UnknownRename,
    DirectoryScopeChanged,
}
