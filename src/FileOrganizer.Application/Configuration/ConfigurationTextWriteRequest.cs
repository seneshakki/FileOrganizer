using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Configuration;

/// <summary>An immutable request for conditional atomic configuration publication.</summary>
public sealed class ConfigurationTextWriteRequest
{
    private readonly ReadOnlyCollection<string> _protectedPaths;

    public ConfigurationTextWriteRequest(
        string path,
        string content,
        ConfigurationWritePrecondition precondition,
        IEnumerable<string>? protectedPaths = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(precondition);

        string[] protectedPathSnapshot = [.. protectedPaths ?? []];
        if (protectedPathSnapshot.Any(static protectedPath => protectedPath is null))
        {
            throw new ArgumentException(
                "Protected configuration paths cannot contain null.",
                nameof(protectedPaths));
        }

        Path = path;
        Content = content;
        Precondition = precondition;
        _protectedPaths = Array.AsReadOnly(protectedPathSnapshot);
    }

    public string Path { get; }

    public string Content { get; }

    public ConfigurationWritePrecondition Precondition { get; }

    /// <summary>
    /// Existing configuration files that the destination must not physically alias.
    /// </summary>
    public IReadOnlyList<string> ProtectedPaths => _protectedPaths;
}
