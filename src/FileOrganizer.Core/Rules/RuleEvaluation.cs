using FileOrganizer.Core.Matching;

namespace FileOrganizer.Core.Rules;

/// <summary>
/// What one rule concluded about one file, and anything that got in the way.
/// </summary>
public sealed class RuleEvaluation
{
    internal RuleEvaluation(
        Rule rule,
        RuleOutcome outcome,
        IReadOnlyList<EvaluationProblem> problems)
    {
        Rule = rule;
        Outcome = outcome;
        Problems = Array.AsReadOnly(problems.ToArray());
    }

    /// <summary>Gets the rule that was evaluated.</summary>
    public Rule Rule { get; }

    /// <summary>Gets what the rule concluded.</summary>
    public RuleOutcome Outcome { get; }

    /// <summary>
    /// Gets the problems met while evaluating this rule. A rule can report
    /// problems and still reach a definite answer.
    /// </summary>
    public IReadOnlyList<EvaluationProblem> Problems { get; }

    /// <summary>Gets whether the rule applies to the file.</summary>
    public bool Applies => Outcome == RuleOutcome.Applies;
}
