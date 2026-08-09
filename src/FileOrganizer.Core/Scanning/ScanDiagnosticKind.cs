namespace FileOrganizer.Core.Scanning;

/// <summary>
/// Identifies why a source scan could not account for every entry in its
/// requested scope.
/// </summary>
/// <remarks>
/// Every diagnostic currently represented here makes a scan incomplete. The
/// planner must never treat an incomplete scan as eligible for execution.
/// </remarks>
public enum ScanDiagnosticKind
{
    /// <summary>The requested source root did not exist.</summary>
    RootNotFound,

    /// <summary>The requested source root existed but was not a directory.</summary>
    RootIsNotDirectory,

    /// <summary>An entry or directory could not be read because access was denied.</summary>
    AccessDenied,

    /// <summary>An entry disappeared after it was discovered.</summary>
    EntryDisappeared,

    /// <summary>An entry changed type after it was discovered.</summary>
    EntryChanged,

    /// <summary>A directory could not be enumerated completely.</summary>
    DirectoryEnumerationFailed,

    /// <summary>A file's required metadata could not be read.</summary>
    MetadataReadFailed,

    /// <summary>A symbolic link, junction, or other reparse point was skipped.</summary>
    ReparsePointSkipped,

    /// <summary>An entry type unsupported by the read-only scanner was skipped.</summary>
    UnsupportedEntrySkipped,

    /// <summary>A path reported by the file system could not be canonicalized.</summary>
    InvalidEntryPath,

    /// <summary>An entry reported while scanning did not remain inside the source root.</summary>
    EntryOutsideRoot,
}
