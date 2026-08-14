namespace FileOrganizer.Application.Configuration;

/// <summary>An immutable, location-aware configuration validation problem.</summary>
public sealed record ConfigurationProblem
{
    public ConfigurationProblem(
        ConfigurationProblemKind kind,
        string location,
        string description,
        Guid? ruleId = null,
        string? discriminator = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (ruleId == Guid.Empty)
        {
            throw new ArgumentException("A problem rule ID cannot be empty.", nameof(ruleId));
        }

        Kind = kind;
        Location = location;
        Description = description;
        RuleId = ruleId;
        Discriminator = discriminator;
    }

    public ConfigurationProblemKind Kind { get; }

    /// <summary>Gets a JSON-pointer-like location within the document.</summary>
    public string Location { get; }

    public string Description { get; }

    public Guid? RuleId { get; }

    public string? Discriminator { get; }
}
