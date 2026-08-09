using System.Collections.ObjectModel;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Rules;

namespace FileOrganizer.Core.Planning;

/// <summary>
/// An immutable ordered rule set in which every matching rule has one action.
/// </summary>
/// <remarks>
/// Matching order is exactly the existing <see cref="RuleSet"/> order: lower
/// <see cref="Rule.Order"/> values run first and input order breaks equal-order
/// ties. That tie order is part of the configured ordered rule set, not file
/// enumeration order.
/// </remarks>
public sealed class OrganizationRuleSet
{
    private readonly Dictionary<Rule, OrganizationRule> _associations;
    private readonly RuleSet _matchingRules;
    private readonly ReadOnlyCollection<OrganizationRule> _rules;

    public OrganizationRuleSet(IEnumerable<OrganizationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        OrganizationRule[] materialized = [.. rules];
        HashSet<Rule> uniqueRules = new(ReferenceEqualityComparer.Instance);

        foreach (OrganizationRule organizationRule in materialized)
        {
            ArgumentNullException.ThrowIfNull(organizationRule, nameof(rules));

            if (!uniqueRules.Add(organizationRule.Rule))
            {
                throw new ArgumentException(
                    "The same matching rule cannot be associated with more than one action.",
                    nameof(rules));
            }
        }

        _matchingRules = new RuleSet(materialized.Select(rule => rule.Rule));
        _associations = new Dictionary<Rule, OrganizationRule>(
            ReferenceEqualityComparer.Instance);
        foreach (OrganizationRule organizationRule in materialized)
        {
            _associations.Add(organizationRule.Rule, organizationRule);
        }

        _rules = Array.AsReadOnly(
            _matchingRules.Rules.Select(rule => _associations[rule]).ToArray());
    }

    /// <summary>Gets associations in their exact first-match evaluation order.</summary>
    public IReadOnlyList<OrganizationRule> Rules => _rules;

    /// <summary>Reports static problems in all matching conditions.</summary>
    public IEnumerable<EvaluationProblem> ValidateRules() => _matchingRules.Validate();

    /// <summary>Reports static action problems once in configured rule order.</summary>
    public IReadOnlyList<OrganizationRuleActionProblem> ValidateActions(PathPolicy pathPolicy)
    {
        ArgumentNullException.ThrowIfNull(pathPolicy);

        OrganizationRuleActionProblem[] problems = _rules
            .SelectMany(
                organizationRule => organizationRule.Action.Validate(pathPolicy)
                    .OrderBy(problem => problem.Kind)
                    .ThenBy(problem => problem.PathProblem.Kind)
                    .ThenBy(problem => problem.Description, StringComparer.Ordinal)
                    .Select(
                        problem => new OrganizationRuleActionProblem(
                            organizationRule,
                            problem)))
            .ToArray();

        return Array.AsReadOnly(problems);
    }

    internal RuleSetEvaluation Evaluate(FileEntry file, EvaluationContext context) =>
        _matchingRules.Evaluate(file, context);

    internal OrganizationRule GetAssociation(Rule rule) => _associations[rule];
}
