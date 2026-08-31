namespace FileOrganizer.Application.Configuration;

public enum ConfigurationWritePreconditionKind
{
    Unconditional,
    MustBeMissing,
    MustMatchRevision,
}
