namespace FileOrganizer.Application.Configuration;

/// <summary>The storage state that must still hold immediately before publication.</summary>
public sealed class ConfigurationWritePrecondition
{
    private ConfigurationWritePrecondition(
        ConfigurationWritePreconditionKind kind,
        ConfigurationDocumentRevision? expectedRevision)
    {
        bool valid = kind switch
        {
            ConfigurationWritePreconditionKind.Unconditional => expectedRevision is null,
            ConfigurationWritePreconditionKind.MustBeMissing => expectedRevision is null,
            ConfigurationWritePreconditionKind.MustMatchRevision => expectedRevision is not null,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The write precondition kind and expected revision are contradictory.",
                nameof(kind));
        }

        Kind = kind;
        ExpectedRevision = expectedRevision;
    }

    public ConfigurationWritePreconditionKind Kind { get; }

    public ConfigurationDocumentRevision? ExpectedRevision { get; }

    public static ConfigurationWritePrecondition Unconditional { get; } =
        new(ConfigurationWritePreconditionKind.Unconditional, null);

    public static ConfigurationWritePrecondition MustBeMissing { get; } =
        new(ConfigurationWritePreconditionKind.MustBeMissing, null);

    public static ConfigurationWritePrecondition MustMatch(
        ConfigurationDocumentRevision expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(expectedRevision);
        return new(ConfigurationWritePreconditionKind.MustMatchRevision, expectedRevision);
    }
}
