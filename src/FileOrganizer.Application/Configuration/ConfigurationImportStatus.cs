namespace FileOrganizer.Application.Configuration;

public enum ConfigurationImportStatus
{
    Imported,
    SourceNotFound,
    Invalid,
    Conflict,
    StorageFailure,
}
