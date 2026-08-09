namespace FileOrganizer.Core.Physical;

/// <summary>A stable explanation for a physical-observation outcome.</summary>
public enum PhysicalEntryObservationReason
{
    /// <summary>No problem occurred.</summary>
    None = 0,

    /// <summary>The entry or one of its path components was missing.</summary>
    EntryMissing,

    /// <summary>The operating system denied the required metadata access.</summary>
    AccessDenied,

    /// <summary>An incompatible open handle prevented safe inspection.</summary>
    SharingViolation,

    /// <summary>A read-only operating-system query failed unexpectedly.</summary>
    InputOutputFailure,

    /// <summary>The host or file system cannot provide the required identity scheme.</summary>
    UnsupportedCapability,

    /// <summary>The final entry is a symbolic link, junction, or other reparse point.</summary>
    ReparsePoint,

    /// <summary>The final entry has a physical kind the application does not support.</summary>
    UnsupportedEntryKind,

    /// <summary>The entry was already pending deletion.</summary>
    DeletePending,

    /// <summary>The entry changed incompatibly while its handle was observed.</summary>
    EntryChanged,

    /// <summary>The supplied path cannot be observed by this adapter.</summary>
    InvalidPath,

    /// <summary>Required size or timestamp metadata was unavailable or invalid.</summary>
    MetadataUnavailable,
}
