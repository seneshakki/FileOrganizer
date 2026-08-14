using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Configuration;

/// <summary>An immutable result from atomic configuration publication.</summary>
public sealed class ConfigurationTextWriteResult
{
    private readonly ReadOnlyCollection<ConfigurationStorageProblem> _problems;

    private ConfigurationTextWriteResult(
        ConfigurationTextWriteStatus status,
        IEnumerable<ConfigurationStorageProblem>? problems)
    {
        ConfigurationStorageProblem[] snapshot = [.. problems ?? []];
        if (snapshot.Any(static problem => problem is null))
        {
            throw new ArgumentException("Storage problems cannot contain null.", nameof(problems));
        }

        bool valid = status switch
        {
            ConfigurationTextWriteStatus.Succeeded => snapshot.Length == 0,
            ConfigurationTextWriteStatus.Failed => snapshot.Length != 0,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                "Configuration write status and problems describe contradictory state.",
                nameof(status));
        }

        Array.Sort(
            snapshot,
            static (left, right) =>
            {
                int kind = left.Kind.CompareTo(right.Kind);
                return kind != 0
                    ? kind
                    : StringComparer.Ordinal.Compare(left.Description, right.Description);
            });

        Status = status;
        _problems = Array.AsReadOnly(snapshot);
    }

    public ConfigurationTextWriteStatus Status { get; }

    public IReadOnlyList<ConfigurationStorageProblem> Problems => _problems;

    public static ConfigurationTextWriteResult Succeeded() =>
        new(ConfigurationTextWriteStatus.Succeeded, null);

    public static ConfigurationTextWriteResult Failed(
        IEnumerable<ConfigurationStorageProblem> problems) =>
        new(ConfigurationTextWriteStatus.Failed, problems);
}
