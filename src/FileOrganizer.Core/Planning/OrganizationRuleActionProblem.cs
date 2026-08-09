using FileOrganizer.Core.Actions;

namespace FileOrganizer.Core.Planning;

/// <summary>Associates a static action problem with its organizational rule.</summary>
public sealed record OrganizationRuleActionProblem
{
    public OrganizationRuleActionProblem(
        OrganizationRule organizationRule,
        ActionValidationProblem problem)
    {
        ArgumentNullException.ThrowIfNull(organizationRule);
        ArgumentNullException.ThrowIfNull(problem);

        OrganizationRule = organizationRule;
        Problem = problem;
    }

    /// <summary>Gets the association containing the invalid action.</summary>
    public OrganizationRule OrganizationRule { get; }

    /// <summary>Gets the exact static action problem.</summary>
    public ActionValidationProblem Problem { get; }
}
