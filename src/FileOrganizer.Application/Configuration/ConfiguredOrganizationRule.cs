using FileOrganizer.Core.Planning;

namespace FileOrganizer.Application.Configuration;

/// <summary>An organization rule paired with its stable persistence identity.</summary>
public sealed class ConfiguredOrganizationRule
{
    public ConfiguredOrganizationRule(Guid id, OrganizationRule rule)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A configured rule ID cannot be empty.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(rule);

        Id = id;
        Rule = rule;
    }

    public Guid Id { get; }

    public OrganizationRule Rule { get; }
}
