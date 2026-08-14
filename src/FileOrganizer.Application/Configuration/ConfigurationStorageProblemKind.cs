namespace FileOrganizer.Application.Configuration;

/// <summary>A storage-boundary failure that does not expose machine-specific paths.</summary>
public enum ConfigurationStorageProblemKind
{
    InvalidPath,
    Inaccessible,
    ReadFailure,
    WriteFailure,
    AtomicPublicationUnavailable,
    PublicationFailure,
}
