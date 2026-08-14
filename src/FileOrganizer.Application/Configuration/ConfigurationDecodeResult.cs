using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Configuration;

/// <summary>The immutable result of decoding and validating one configuration document.</summary>
public sealed class ConfigurationDecodeResult
{
    private readonly ReadOnlyCollection<ConfigurationProblem> _problems;

    private ConfigurationDecodeResult(
        OrganizationConfiguration? configuration,
        IEnumerable<ConfigurationProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        ConfigurationProblem[] snapshot = [.. problems];
        if (snapshot.Any(static problem => problem is null))
        {
            throw new ArgumentException("Configuration problems cannot contain null.", nameof(problems));
        }

        Array.Sort(snapshot, ConfigurationProblemComparer.Instance);
        if ((configuration is null) == (snapshot.Length == 0))
        {
            throw new ArgumentException(
                "A successful decode has configuration and no problems; a rejection has problems and no configuration.",
                nameof(problems));
        }

        Configuration = configuration;
        _problems = Array.AsReadOnly(snapshot);
    }

    public bool Succeeded => Configuration is not null;

    public OrganizationConfiguration? Configuration { get; }

    public IReadOnlyList<ConfigurationProblem> Problems => _problems;

    internal static ConfigurationDecodeResult Success(OrganizationConfiguration configuration) =>
        new(configuration, []);

    internal static ConfigurationDecodeResult Invalid(IEnumerable<ConfigurationProblem> problems) =>
        new(null, problems);

    private sealed class ConfigurationProblemComparer : IComparer<ConfigurationProblem>
    {
        internal static ConfigurationProblemComparer Instance { get; } = new();

        public int Compare(ConfigurationProblem? left, ConfigurationProblem? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int location = StringComparer.Ordinal.Compare(left.Location, right.Location);
            if (location != 0)
            {
                return location;
            }

            int kind = left.Kind.CompareTo(right.Kind);
            return kind != 0
                ? kind
                : StringComparer.Ordinal.Compare(left.Description, right.Description);
        }
    }
}
