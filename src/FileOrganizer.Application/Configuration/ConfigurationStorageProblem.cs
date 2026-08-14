namespace FileOrganizer.Application.Configuration;

/// <summary>An immutable problem reported by a physical configuration store.</summary>
public sealed record ConfigurationStorageProblem
{
    public ConfigurationStorageProblem(
        ConfigurationStorageProblemKind kind,
        string description)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Kind = kind;
        Description = description;
    }

    public ConfigurationStorageProblemKind Kind { get; }

    public string Description { get; }
}
