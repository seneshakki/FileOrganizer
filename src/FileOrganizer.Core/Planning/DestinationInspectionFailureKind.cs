namespace FileOrganizer.Core.Planning;

/// <summary>Why a destination inspection could not produce a trustworthy fact.</summary>
public enum DestinationInspectionFailureKind
{
    /// <summary>The process was not permitted to inspect the destination.</summary>
    AccessDenied,

    /// <summary>The entry changed or disappeared while it was being inspected.</summary>
    EntryChanged,

    /// <summary>The destination is a link, reparse point, or another unsupported entry.</summary>
    UnsupportedEntry,

    /// <summary>The platform rejected the path supplied by Core.</summary>
    InvalidPath,

    /// <summary>Another input/output failure prevented a reliable answer.</summary>
    InputOutputFailure,
}
