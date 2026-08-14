using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Configuration;

/// <summary>The immutable outcome of loading and validating a configuration file.</summary>
public sealed class ConfigurationLoadResult
{
    private readonly ReadOnlyCollection<ConfigurationProblem> _configurationProblems;
    private readonly ReadOnlyCollection<ConfigurationStorageProblem> _storageProblems;

    private ConfigurationLoadResult(
        ConfigurationLoadStatus status,
        OrganizationConfiguration? configuration,
        IEnumerable<ConfigurationProblem>? configurationProblems,
        IEnumerable<ConfigurationStorageProblem>? storageProblems)
    {
        ConfigurationProblem[] configurationSnapshot = [.. configurationProblems ?? []];
        ConfigurationStorageProblem[] storageSnapshot = [.. storageProblems ?? []];
        bool valid = status switch
        {
            ConfigurationLoadStatus.Loaded =>
                configuration is not null
                && configurationSnapshot.Length == 0
                && storageSnapshot.Length == 0,
            ConfigurationLoadStatus.NotFound =>
                configuration is null
                && configurationSnapshot.Length == 0
                && storageSnapshot.Length == 0,
            ConfigurationLoadStatus.Invalid =>
                configuration is null
                && configurationSnapshot.Length != 0
                && storageSnapshot.Length == 0,
            ConfigurationLoadStatus.StorageFailure =>
                configuration is null
                && configurationSnapshot.Length == 0
                && storageSnapshot.Length != 0,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("Configuration load state is contradictory.", nameof(status));
        }

        Status = status;
        Configuration = configuration;
        _configurationProblems = Array.AsReadOnly(configurationSnapshot);
        _storageProblems = Array.AsReadOnly(storageSnapshot);
    }

    public ConfigurationLoadStatus Status { get; }

    public OrganizationConfiguration? Configuration { get; }

    public IReadOnlyList<ConfigurationProblem> ConfigurationProblems => _configurationProblems;

    public IReadOnlyList<ConfigurationStorageProblem> StorageProblems => _storageProblems;

    internal static ConfigurationLoadResult Loaded(OrganizationConfiguration configuration) =>
        new(ConfigurationLoadStatus.Loaded, configuration, null, null);

    internal static ConfigurationLoadResult NotFound() =>
        new(ConfigurationLoadStatus.NotFound, null, null, null);

    internal static ConfigurationLoadResult Invalid(IEnumerable<ConfigurationProblem> problems) =>
        new(ConfigurationLoadStatus.Invalid, null, problems, null);

    internal static ConfigurationLoadResult StorageFailure(
        IEnumerable<ConfigurationStorageProblem> problems) =>
        new(ConfigurationLoadStatus.StorageFailure, null, null, problems);
}
