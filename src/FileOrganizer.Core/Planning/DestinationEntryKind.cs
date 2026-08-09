namespace FileOrganizer.Core.Planning;

/// <summary>What a read-only inspection found at one proposed destination.</summary>
public enum DestinationEntryKind
{
    /// <summary>No entry existed at the inspected path.</summary>
    Missing,

    /// <summary>A file existed at the inspected path.</summary>
    File,

    /// <summary>A directory existed at the inspected path.</summary>
    Directory,

    /// <summary>The path could not be inspected reliably.</summary>
    InspectionFailed,
}
