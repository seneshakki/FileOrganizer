namespace FileOrganizer.Application.Configuration;

public enum ConfigurationLoadStatus
{
    Loaded,
    NotFound,
    Invalid,
    StorageFailure,
}
