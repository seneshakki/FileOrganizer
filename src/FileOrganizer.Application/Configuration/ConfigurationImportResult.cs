using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Configuration;

/// <summary>The immutable outcome of validated, replace-not-merge import.</summary>
public sealed class ConfigurationImportResult
{
    private readonly ReadOnlyCollection<ConfigurationProblem> _configurationProblems;
    private readonly ReadOnlyCollection<ConfigurationStorageProblem> _storageProblems;

    private ConfigurationImportResult(
        ConfigurationImportStatus status,
        OrganizationConfiguration? configuration,
        ConfigurationDocumentRevision? revision,
        ConfigurationWriteConflict? conflict,
        IEnumerable<ConfigurationProblem>? configurationProblems,
        IEnumerable<ConfigurationStorageProblem>? storageProblems)
    {
        ConfigurationProblem[] configurationSnapshot = [.. configurationProblems ?? []];
        ConfigurationStorageProblem[] storageSnapshot = [.. storageProblems ?? []];
        bool valid = status switch
        {
            ConfigurationImportStatus.Imported =>
                configuration is not null
                && revision is not null
                && conflict is null
                && configurationSnapshot.Length == 0
                && storageSnapshot.Length == 0,
            ConfigurationImportStatus.SourceNotFound =>
                configuration is null
                && revision is null
                && conflict is null
                && configurationSnapshot.Length == 0
                && storageSnapshot.Length == 0,
            ConfigurationImportStatus.Invalid =>
                configuration is null
                && revision is null
                && conflict is null
                && configurationSnapshot.Length != 0
                && storageSnapshot.Length == 0,
            ConfigurationImportStatus.Conflict =>
                configuration is null
                && revision is null
                && conflict is not null
                && configurationSnapshot.Length == 0
                && storageSnapshot.Length == 0,
            ConfigurationImportStatus.StorageFailure =>
                configuration is null
                && revision is null
                && conflict is null
                && configurationSnapshot.Length == 0
                && storageSnapshot.Length != 0,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("Configuration import state is contradictory.", nameof(status));
        }

        Status = status;
        Configuration = configuration;
        Revision = revision;
        Conflict = conflict;
        _configurationProblems = Array.AsReadOnly(configurationSnapshot);
        _storageProblems = Array.AsReadOnly(storageSnapshot);
    }

    public ConfigurationImportStatus Status { get; }

    public OrganizationConfiguration? Configuration { get; }

    public ConfigurationDocumentRevision? Revision { get; }

    public ConfigurationWriteConflict? Conflict { get; }

    public IReadOnlyList<ConfigurationProblem> ConfigurationProblems => _configurationProblems;

    public IReadOnlyList<ConfigurationStorageProblem> StorageProblems => _storageProblems;

    internal static ConfigurationImportResult Imported(
        OrganizationConfiguration configuration,
        ConfigurationDocumentRevision revision) =>
        new(ConfigurationImportStatus.Imported, configuration, revision, null, null, null);

    internal static ConfigurationImportResult SourceNotFound() =>
        new(ConfigurationImportStatus.SourceNotFound, null, null, null, null, null);

    internal static ConfigurationImportResult Invalid(IEnumerable<ConfigurationProblem> problems) =>
        new(ConfigurationImportStatus.Invalid, null, null, null, problems, null);

    internal static ConfigurationImportResult Conflicted(ConfigurationWriteConflict conflict) =>
        new(ConfigurationImportStatus.Conflict, null, null, conflict, null, null);

    internal static ConfigurationImportResult StorageFailure(
        IEnumerable<ConfigurationStorageProblem> problems) =>
        new(ConfigurationImportStatus.StorageFailure, null, null, null, null, problems);
}
