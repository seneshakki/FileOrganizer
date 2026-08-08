using FileOrganizer.Core.Matching;

namespace FileOrganizer.Core.Rules;

/// <summary>
/// What a rule set concluded about one file, and how it got there.
/// </summary>
public sealed class RuleSetEvaluation
{
    internal RuleSetEvaluation(
        RuleSetOutcome outcome,
        Rule? decidingRule,
        IReadOnlyList<RuleEvaluation> evaluations,
        IReadOnlyList<EvaluationProblem> problems)
    {
        Outcome = outcome;
        DecidingRule = decidingRule;
        Evaluations = evaluations;
        Problems = problems;
    }

    /// <summary>Gets what the rule set concluded.</summary>
    public RuleSetOutcome Outcome { get; }

    /// <summary>
    /// Gets the rule that decided the result: the one that applies, or the one
    /// that could not be decided. <see langword="null"/> when no rule applied.
    /// </summary>
    public Rule? DecidingRule { get; }

    /// <summary>
    /// Gets the rules that were actually evaluated, in the order they were
    /// evaluated, ending with the one that decided the result.
    /// </summary>
    /// <remarks>
    /// Rules after the deciding one are not listed, because they were never
    /// asked.
    /// </remarks>
    public IReadOnlyList<RuleEvaluation> Evaluations { get; }

    /// <summary>Gets every problem met while evaluating, across all the rules.</summary>
    public IReadOnlyList<EvaluationProblem> Problems { get; }
}
