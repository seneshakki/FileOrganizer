using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Matching.Logical;

/// <summary>
/// Matches when every condition it combines matches.
/// </summary>
/// <remarks>
/// With three possible answers, "every" needs care. One definite non-match
/// settles the whole conjunction no matter what the undecided conditions would
/// have said, so a rule stays usable even when part of it is broken — as long
/// as the broken part could only ever have narrowed the result. A match, on the
/// other hand, requires every condition to have been decided.
/// </remarks>
public sealed class AndCondition : CompositeCondition
{
    public AndCondition(params ICondition[] children)
        : base(children)
    {
    }

    /// <inheritdoc />
    protected override ConditionResult EvaluateCore(FileEntry file, EvaluationContext context)
    {
        List<EvaluationProblem>? problems = null;
        bool anyUndetermined = false;

        foreach (ICondition child in Children)
        {
            ConditionResult result = child.Evaluate(file, context);
            Collect(ref problems, result);

            switch (result.Outcome)
            {
                case MatchOutcome.NoMatch:
                    // Settled. The remaining conditions are not evaluated, which
                    // means their problems go unreported for this file; that is
                    // the price of not running a slow pattern for an answer that
                    // cannot change.
                    return ConditionResult.Create(MatchOutcome.NoMatch, problems);

                case MatchOutcome.Indeterminate:
                    anyUndetermined = true;
                    break;
            }
        }

        return ConditionResult.Create(
            anyUndetermined ? MatchOutcome.Indeterminate : MatchOutcome.Match,
            problems);
    }
}
