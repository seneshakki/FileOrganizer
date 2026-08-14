using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Configuration;

/// <summary>The immutable output of deliberate domain-to-JSON mapping.</summary>
public sealed class ConfigurationEncodeResult
{
    private readonly ReadOnlyCollection<ConfigurationProblem> _problems;

    internal ConfigurationEncodeResult(
        string? json,
        IEnumerable<ConfigurationProblem>? problems = null)
    {
        ConfigurationProblem[] snapshot = [.. problems ?? []];
        if ((json is null) == (snapshot.Length == 0))
        {
            throw new ArgumentException(
                "Successful encoding has JSON and no problems; rejected encoding has problems and no JSON.",
                nameof(problems));
        }

        Json = json;
        _problems = Array.AsReadOnly(snapshot);
    }

    public bool Succeeded => Json is not null;

    public string? Json { get; }

    public IReadOnlyList<ConfigurationProblem> Problems => _problems;
}
