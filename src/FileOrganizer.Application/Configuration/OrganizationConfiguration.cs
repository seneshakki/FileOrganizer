using System.Collections.ObjectModel;
using System.Text;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Rules;

namespace FileOrganizer.Application.Configuration;

/// <summary>The immutable, runtime-safe configuration reconstructed from a configuration document.</summary>
public sealed class OrganizationConfiguration
{
    private readonly ReadOnlyCollection<ConfiguredOrganizationRule> _rules;
    private readonly ReadOnlyCollection<WatchRootConfiguration> _watchRoots;

    public OrganizationConfiguration(
        PathSemantics pathSemantics,
        IEnumerable<ConfiguredOrganizationRule> rules)
        : this(pathSemantics, rules, [])
    {
    }

    public OrganizationConfiguration(
        PathSemantics pathSemantics,
        IEnumerable<ConfiguredOrganizationRule> rules,
        IEnumerable<WatchRootConfiguration> watchRoots)
    {
        if (!Enum.IsDefined(pathSemantics.Syntax)
            || !Enum.IsDefined(pathSemantics.CaseSensitivity)
            || !Enum.IsDefined(pathSemantics.UnicodeNormalization))
        {
            throw new ArgumentException("Path semantics must use known values.", nameof(pathSemantics));
        }

        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(watchRoots);

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

        WatchRootConfiguration[] givenWatchRoots = [.. watchRoots];
        if (givenWatchRoots.Any(static watchRoot => watchRoot is null))
        {
            throw new ArgumentException("Watch roots cannot contain null.", nameof(watchRoots));
        }

        if (givenWatchRoots.Select(static watchRoot => watchRoot.Id).Distinct().Count()
            != givenWatchRoots.Length)
        {
            throw new ArgumentException("Watch-root IDs must be unique.", nameof(watchRoots));
        }

        PathPolicy pathPolicy = new(
            pathSemantics.Syntax,
            pathSemantics.CaseSensitivity,
            pathSemantics.UnicodeNormalization);
        HashSet<CanonicalPath> rootPaths = new(pathPolicy.LogicalComparer);
        UTF8Encoding strictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        foreach (WatchRootConfiguration watchRoot in givenWatchRoots)
        {
            if (watchRoot.RootPath.Semantics != pathSemantics)
            {
                throw new ArgumentException(
                    "Watch-root paths must use the configured path semantics.",
                    nameof(watchRoots));
            }

            PathResolution resolution = pathPolicy.CanonicalizeAbsolute(watchRoot.RootPath.Value);
            if (!resolution.Succeeded
                || !string.Equals(
                    resolution.Path!.Value,
                    watchRoot.RootPath.Value,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Watch-root paths must be fully qualified canonical paths.",
                    nameof(watchRoots));
            }

            try
            {
                _ = strictUtf8.GetByteCount(watchRoot.RootPath.Value);
            }
            catch (EncoderFallbackException exception)
            {
                throw new ArgumentException(
                    "Watch-root paths must contain valid Unicode.",
                    nameof(watchRoots),
                    exception);
            }

            if (!rootPaths.Add(watchRoot.RootPath))
            {
                throw new ArgumentException(
                    "Watch-root paths must be logically unique under the configured path policy.",
                    nameof(watchRoots));
            }
        }

        PathSemantics = pathSemantics;
        _rules = Array.AsReadOnly(ordered);
        _watchRoots = Array.AsReadOnly((WatchRootConfiguration[])givenWatchRoots.Clone());
        RuleSet = new OrganizationRuleSet(ordered.Select(static configured => configured.Rule));
    }

    public PathSemantics PathSemantics { get; }

    /// <summary>Gets rules in their exact first-match evaluation order.</summary>
    public IReadOnlyList<ConfiguredOrganizationRule> Rules => _rules;

    /// <summary>Gets watch roots in their configured order.</summary>
    public IReadOnlyList<WatchRootConfiguration> WatchRoots => _watchRoots;

    /// <summary>Gets the Core rule set used for deterministic planning.</summary>
    public OrganizationRuleSet RuleSet { get; }
}
