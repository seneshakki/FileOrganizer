namespace FileOrganizer.Application.Configuration;

public enum ConfigurationWriteConflictKind
{
    TargetAlreadyExists,
    ChangedExternally,
    ProtectedPathAlias,
    ProtectedPathIdentityUnavailable,
}
