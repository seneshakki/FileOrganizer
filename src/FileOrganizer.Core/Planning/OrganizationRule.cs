using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Rules;

namespace FileOrganizer.Core.Planning;

/// <summary>
/// Associates one matching rule with the read-only action intent it selects.
/// </summary>
public sealed class OrganizationRule
{
    public OrganizationRule(Rule rule, OrganizationAction action)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(action);

        Rule = rule;
        Action = action;
    }

    /// <summary>Gets the rule that decides whether this association applies.</summary>
    public Rule Rule { get; }

    /// <summary>Gets the action intent selected when the rule applies.</summary>
    public OrganizationAction Action { get; }
}
