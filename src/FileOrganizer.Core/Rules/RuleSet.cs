using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching;

namespace FileOrganizer.Core.Rules;

/// <summary>
/// An ordered collection of rules, evaluated first-match-wins.
/// </summary>
/// <remarks>
/// Order is explicit and total. Rules are sorted by <see cref="Rule.Order"/>
/// ascending, and rules sharing an order keep the sequence they were given in.
/// The sorted sequence is fixed when the set is built and is what
/// <see cref="Rules"/> reports, so what a user sees in a list is what will run.
///
/// Evaluation stops at the first rule that applies. It also stops at the first
/// rule that cannot be decided, and reports the file as blocked rather than
/// carrying on: a lower-priority rule must not act on a file when a rule that
/// outranks it might have claimed that file first.
/// </remarks>
public sealed class RuleSet
{
    private readonly Rule[] _rules;

    /// <param name="rules">
    /// The rules, in any order. They are sorted here; the caller's sequence only
    /// breaks ties.
    /// </param>
    public RuleSet(IEnumerable<Rule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        Rule[] given = [.. rules];

        foreach (Rule rule in given)
        {
            ArgumentNullException.ThrowIfNull(rule, nameof(rules));
        }

        // OrderBy is a stable sort, which is what makes equal orders keep the
        // sequence they arrived in.
        _rules = [.. given.OrderBy(rule => rule.Order)];
    }

    /// <summary>Gets the rules in the order they will be evaluated.</summary>
    public IReadOnlyList<Rule> Rules => _rules;

    /// <summary>
    /// Reports everything wrong with the rules that can be found without looking
    /// at a file, disabled rules included.
    /// </summary>
    public IEnumerable<EvaluationProblem> Validate() =>
        _rules.SelectMany(rule => rule.Validate());

    /// <summary>Works out which rule, if any, claims a file.</summary>
    public RuleSetEvaluation Evaluate(FileEntry file, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(context);

        List<RuleEvaluation> evaluations = [];
        List<EvaluationProblem>? problems = null;

        foreach (Rule rule in _rules)
        {
            RuleEvaluation evaluation = rule.Evaluate(file, context);
            evaluations.Add(evaluation);

            if (evaluation.Problems.Count != 0)
            {
                problems ??= [];
                problems.AddRange(evaluation.Problems);
            }

            switch (evaluation.Outcome)
            {
                case RuleOutcome.Applies:
                    return Build(RuleSetOutcome.RuleSelected, rule, evaluations, problems);

                case RuleOutcome.Indeterminate:
                    return Build(RuleSetOutcome.Blocked, rule, evaluations, problems);
            }
        }

        return Build(RuleSetOutcome.NoRuleApplies, null, evaluations, problems);
    }

    private static RuleSetEvaluation Build(
        RuleSetOutcome outcome,
        Rule? decidingRule,
        List<RuleEvaluation> evaluations,
        List<EvaluationProblem>? problems) =>
        new(outcome, decidingRule, evaluations, problems ?? (IReadOnlyList<EvaluationProblem>)[]);
}
