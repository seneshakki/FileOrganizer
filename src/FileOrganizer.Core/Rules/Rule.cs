using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching;

namespace FileOrganizer.Core.Rules;

/// <summary>
/// One named decision: which files this rule claims, subject to an exclusion
/// condition that can veto the rule without first evaluating inclusion.
/// </summary>
/// <remarks>
/// A rule applies to a file when its inclusion conditions match and its
/// exclusion conditions do not. Exclusion is checked first and wins outright.
/// A result of <see cref="RuleOutcome.Excluded"/> therefore means the exclusion
/// matched; it deliberately makes no claim about inclusion, which was not
/// evaluated because it could not change the decision.
///
/// The same reasoning decides what happens when something cannot be decided. If
/// exclusion is undecidable, the rule is undecidable too, even if inclusion
/// matched: acting would risk touching a file the user meant to protect. But if
/// inclusion definitely does not match, the rule definitely does not apply, no
/// matter what the exclusion could not tell us.
/// </remarks>
public sealed class Rule
{
    /// <param name="name">A name for the rule, shown to the user.</param>
    /// <param name="include">The conditions a file must match.</param>
    /// <param name="exclude">
    /// Conditions that veto this rule for a file, or <see langword="null"/>
    /// when the rule has no veto.
    /// </param>
    /// <param name="isEnabled">Whether the rule is considered at all.</param>
    /// <param name="order">
    /// Where this rule sits relative to the others. Lower runs first.
    /// </param>
    public Rule(
        string name,
        ICondition include,
        ICondition? exclude = null,
        bool isEnabled = true,
        int order = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(include);

        Name = name;
        Include = include;
        Exclude = exclude;
        IsEnabled = isEnabled;
        Order = order;
    }

    /// <summary>Gets the name shown to the user.</summary>
    public string Name { get; }

    /// <summary>Gets the conditions a file must match.</summary>
    public ICondition Include { get; }

    /// <summary>Gets the conditions that veto this rule for a file, if any.</summary>
    public ICondition? Exclude { get; }

    /// <summary>Gets whether the rule is considered when files are evaluated.</summary>
    public bool IsEnabled { get; }

    /// <summary>Gets the rule's position relative to the others; lower runs first.</summary>
    public int Order { get; }

    /// <summary>
    /// Reports everything wrong with this rule that can be found without looking
    /// at a file.
    /// </summary>
    /// <remarks>
    /// A disabled rule is still validated. Someone editing a rule set wants to
    /// see that a switched-off rule is broken before they switch it on, not
    /// after.
    /// </remarks>
    public IEnumerable<EvaluationProblem> Validate() =>
        Include.Validate().Concat(Exclude?.Validate() ?? []);

    /// <summary>Works out whether this rule applies to a file.</summary>
    public RuleEvaluation Evaluate(FileEntry file, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(context);

        if (!IsEnabled)
        {
            return new RuleEvaluation(this, RuleOutcome.Disabled, []);
        }

        // Exclusion first: a definite exclusion settles the rule whatever the
        // inclusion conditions would have said. Excluded records that veto; it
        // does not imply that inclusion was evaluated or matched.
        ConditionResult excluded = Exclude is null
            ? ConditionResult.NoMatch
            : Exclude.Evaluate(file, context);

        if (excluded.Outcome == MatchOutcome.Match)
        {
            return new RuleEvaluation(this, RuleOutcome.Excluded, excluded.Problems);
        }

        ConditionResult included = Include.Evaluate(file, context);
        IReadOnlyList<EvaluationProblem> problems = Combine(excluded, included);

        if (included.Outcome == MatchOutcome.NoMatch)
        {
            return new RuleEvaluation(this, RuleOutcome.NotMatched, problems);
        }

        bool anythingUndecided =
            included.Outcome == MatchOutcome.Indeterminate
            || excluded.Outcome == MatchOutcome.Indeterminate;

        return new RuleEvaluation(
            this,
            anythingUndecided ? RuleOutcome.Indeterminate : RuleOutcome.Applies,
            problems);
    }

    private static IReadOnlyList<EvaluationProblem> Combine(
        ConditionResult first,
        ConditionResult second)
    {
        if (first.Problems.Count == 0)
        {
            return second.Problems;
        }

        if (second.Problems.Count == 0)
        {
            return first.Problems;
        }

        return [.. first.Problems, .. second.Problems];
    }
}
