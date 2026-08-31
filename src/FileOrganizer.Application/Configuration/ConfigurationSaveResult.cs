using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Configuration;

/// <summary>The immutable outcome of validating and atomically saving configuration.</summary>
public sealed class ConfigurationSaveResult
{
    private readonly ReadOnlyCollection<ConfigurationProblem> _configurationProblems;
    private readonly ReadOnlyCollection<ConfigurationStorageProblem> _storageProblems;

    private ConfigurationSaveResult(
        ConfigurationSaveStatus status,
        ConfigurationDocumentRevision? revision,
        ConfigurationWriteConflict? conflict,
        IEnumerable<ConfigurationProblem>? configurationProblems,
        IEnumerable<ConfigurationStorageProblem>? storageProblems)
    {
        ConfigurationProblem[] configurationSnapshot = [.. configurationProblems ?? []];
        ConfigurationStorageProblem[] storageSnapshot = [.. storageProblems ?? []];
        bool valid = status switch
        {
            ConfigurationSaveStatus.Saved =>
                revision is not null
                && conflict is null
                && configurationSnapshot.Length == 0
                && storageSnapshot.Length == 0,
            ConfigurationSaveStatus.Invalid =>
                revision is null
                && conflict is null
                && configurationSnapshot.Length != 0
                && storageSnapshot.Length == 0,
            ConfigurationSaveStatus.Conflict =>
                revision is null
                && conflict is not null
                && configurationSnapshot.Length == 0
                && storageSnapshot.Length == 0,
            ConfigurationSaveStatus.StorageFailure =>
                revision is null
                && conflict is null
                && configurationSnapshot.Length == 0
                && storageSnapshot.Length != 0,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("Configuration save state is contradictory.", nameof(status));
        }

        Status = status;
        Revision = revision;
        Conflict = conflict;
        _configurationProblems = Array.AsReadOnly(configurationSnapshot);
        _storageProblems = Array.AsReadOnly(storageSnapshot);
    }

    public ConfigurationSaveStatus Status { get; }

    public ConfigurationDocumentRevision? Revision { get; }

    public ConfigurationWriteConflict? Conflict { get; }

    public IReadOnlyList<ConfigurationProblem> ConfigurationProblems => _configurationProblems;

    public IReadOnlyList<ConfigurationStorageProblem> StorageProblems => _storageProblems;

    internal static ConfigurationSaveResult Saved(ConfigurationDocumentRevision revision) =>
        new(ConfigurationSaveStatus.Saved, revision, null, null, null);

    internal static ConfigurationSaveResult Invalid(IEnumerable<ConfigurationProblem> problems) =>
        new(ConfigurationSaveStatus.Invalid, null, null, problems, null);

    internal static ConfigurationSaveResult Conflicted(ConfigurationWriteConflict conflict) =>
        new(ConfigurationSaveStatus.Conflict, null, conflict, null, null);

    internal static ConfigurationSaveResult StorageFailure(
        IEnumerable<ConfigurationStorageProblem> problems) =>
        new(ConfigurationSaveStatus.StorageFailure, null, null, null, problems);
}
