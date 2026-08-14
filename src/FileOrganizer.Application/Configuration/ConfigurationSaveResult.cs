using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Configuration;

/// <summary>The immutable outcome of validating and atomically saving configuration.</summary>
public sealed class ConfigurationSaveResult
{
    private readonly ReadOnlyCollection<ConfigurationProblem> _configurationProblems;
    private readonly ReadOnlyCollection<ConfigurationStorageProblem> _storageProblems;

    private ConfigurationSaveResult(
        ConfigurationSaveStatus status,
        IEnumerable<ConfigurationProblem>? configurationProblems,
        IEnumerable<ConfigurationStorageProblem>? storageProblems)
    {
        ConfigurationProblem[] configurationSnapshot = [.. configurationProblems ?? []];
        ConfigurationStorageProblem[] storageSnapshot = [.. storageProblems ?? []];
        bool valid = status switch
        {
            ConfigurationSaveStatus.Saved =>
                configurationSnapshot.Length == 0 && storageSnapshot.Length == 0,
            ConfigurationSaveStatus.Invalid =>
                configurationSnapshot.Length != 0 && storageSnapshot.Length == 0,
            ConfigurationSaveStatus.StorageFailure =>
                configurationSnapshot.Length == 0 && storageSnapshot.Length != 0,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("Configuration save state is contradictory.", nameof(status));
        }

        Status = status;
        _configurationProblems = Array.AsReadOnly(configurationSnapshot);
        _storageProblems = Array.AsReadOnly(storageSnapshot);
    }

    public ConfigurationSaveStatus Status { get; }

    public IReadOnlyList<ConfigurationProblem> ConfigurationProblems => _configurationProblems;

    public IReadOnlyList<ConfigurationStorageProblem> StorageProblems => _storageProblems;

    internal static ConfigurationSaveResult Saved() =>
        new(ConfigurationSaveStatus.Saved, null, null);

    internal static ConfigurationSaveResult Invalid(IEnumerable<ConfigurationProblem> problems) =>
        new(ConfigurationSaveStatus.Invalid, problems, null);

    internal static ConfigurationSaveResult StorageFailure(
        IEnumerable<ConfigurationStorageProblem> problems) =>
        new(ConfigurationSaveStatus.StorageFailure, null, problems);
}
