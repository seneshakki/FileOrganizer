namespace FileOrganizer.Application.Configuration;

/// <summary>A fail-safe conditional-publication conflict without machine-specific paths.</summary>
public sealed record ConfigurationWriteConflict
{
    public ConfigurationWriteConflict(
        ConfigurationWriteConflictKind kind,
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

    public ConfigurationWriteConflictKind Kind { get; }

    public string Description { get; }
}
