using System.Collections.ObjectModel;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Rules;

namespace FileOrganizer.Application.Configuration;

/// <summary>The immutable, runtime-safe configuration reconstructed from a configuration document.</summary>
public sealed class OrganizationConfiguration
{
    private readonly ReadOnlyCollection<ConfiguredOrganizationRule> _rules;

    public OrganizationConfiguration(
        PathSemantics pathSemantics,
        IEnumerable<ConfiguredOrganizationRule> rules)
    {
        if (!Enum.IsDefined(pathSemantics.Syntax)
            || !Enum.IsDefined(pathSemantics.CaseSensitivity)
            || !Enum.IsDefined(pathSemantics.UnicodeNormalization))
        {
            throw new ArgumentException("Path semantics must use known values.", nameof(pathSemantics));
        }

        ArgumentNullException.ThrowIfNull(rules);

        ConfiguredOrganizationRule[] given = [.. rules];
        if (given.Any(static rule => rule is null))
        {
            throw new ArgumentException("Configured rules cannot contain null.", nameof(rules));
        }

        if (given.Select(static rule => rule.Id).Distinct().Count() != given.Length)
        {
            throw new ArgumentException("Configured rule IDs must be unique.", nameof(rules));
        }

        HashSet<Rule> matchingRules = new(ReferenceEqualityComparer.Instance);
        if (given.Any(rule => !matchingRules.Add(rule.Rule.Rule)))
        {
            throw new ArgumentException(
                "The same matching rule cannot appear more than once.",
                nameof(rules));
        }

        ConfiguredOrganizationRule[] ordered = given
            .Select(static (rule, index) => (Rule: rule, Index: index))
            .OrderBy(static entry => entry.Rule.Rule.Rule.Order)
            .ThenBy(static entry => entry.Index)
            .Select(static entry => entry.Rule)
            .ToArray();

        PathSemantics = pathSemantics;
        _rules = Array.AsReadOnly(ordered);
        RuleSet = new OrganizationRuleSet(ordered.Select(static configured => configured.Rule));
    }

    public PathSemantics PathSemantics { get; }

    /// <summary>Gets rules in their exact first-match evaluation order.</summary>
    public IReadOnlyList<ConfiguredOrganizationRule> Rules => _rules;

    /// <summary>Gets the Core rule set used for deterministic planning.</summary>
    public OrganizationRuleSet RuleSet { get; }
}
