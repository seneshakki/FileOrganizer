using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Configuration;

/// <summary>An immutable result from the physical configuration read boundary.</summary>
public sealed class ConfigurationTextReadResult
{
    private readonly ReadOnlyCollection<ConfigurationStorageProblem> _problems;

    private ConfigurationTextReadResult(
        ConfigurationTextReadStatus status,
        string? content,
        ConfigurationDocumentRevision? revision,
        IEnumerable<ConfigurationStorageProblem>? problems)
    {
        ConfigurationStorageProblem[] snapshot = Snapshot(problems);
        bool valid = status switch
        {
            ConfigurationTextReadStatus.Found =>
                content is not null && revision is not null && snapshot.Length == 0,
            ConfigurationTextReadStatus.NotFound =>
                content is null && revision is null && snapshot.Length == 0,
            ConfigurationTextReadStatus.Failed =>
                content is null && revision is null && snapshot.Length != 0,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                "Configuration read status, content, and problems describe contradictory state.",
                nameof(status));
        }

        Status = status;
        Content = content;
        Revision = revision;
        _problems = Array.AsReadOnly(snapshot);
    }

    public ConfigurationTextReadStatus Status { get; }

    public string? Content { get; }

    public ConfigurationDocumentRevision? Revision { get; }

    public IReadOnlyList<ConfigurationStorageProblem> Problems => _problems;

    public static ConfigurationTextReadResult Found(
        string content,
        ConfigurationDocumentRevision revision)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(revision);
        return new(ConfigurationTextReadStatus.Found, content, revision, null);
    }

    public static ConfigurationTextReadResult NotFound() =>
        new(ConfigurationTextReadStatus.NotFound, null, null, null);

    public static ConfigurationTextReadResult Failed(
        IEnumerable<ConfigurationStorageProblem> problems) =>
        new(ConfigurationTextReadStatus.Failed, null, null, problems);

    private static ConfigurationStorageProblem[] Snapshot(
        IEnumerable<ConfigurationStorageProblem>? problems)
    {
        ConfigurationStorageProblem[] snapshot = [.. problems ?? []];
        if (snapshot.Any(static problem => problem is null))
        {
            throw new ArgumentException("Storage problems cannot contain null.", nameof(problems));
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
        return snapshot;
    }
}
