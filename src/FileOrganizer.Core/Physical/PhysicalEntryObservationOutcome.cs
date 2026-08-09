namespace FileOrganizer.Core.Physical;

/// <summary>The structured outcome of one read-only physical observation.</summary>
public enum PhysicalEntryObservationOutcome
{
    /// <summary>Kind, strong identity, and required metadata were captured.</summary>
    Success = 0,

    /// <summary>The requested entry did not exist when opened.</summary>
    Missing,

    /// <summary>The entry could not be inspected safely.</summary>
    Inaccessible,

    /// <summary>The platform, file system, or entry kind cannot supply the guarantee.</summary>
    Unsupported,
}
