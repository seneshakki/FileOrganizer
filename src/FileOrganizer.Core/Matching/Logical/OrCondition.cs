using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Matching.Logical;

/// <summary>
/// Matches when at least one of the conditions it combines matches.
/// </summary>
/// <remarks>
/// The mirror of <see cref="AndCondition"/>: one definite match settles the
/// disjunction regardless of what could not be decided, but concluding that
/// nothing matched requires every condition to have been decided.
/// </remarks>
public sealed class OrCondition : CompositeCondition
{
    public OrCondition(params ICondition[] children)
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
                case MatchOutcome.Match:
                    return ConditionResult.Create(MatchOutcome.Match, problems);

                case MatchOutcome.Indeterminate:
                    anyUndetermined = true;
                    break;
            }
        }

        return ConditionResult.Create(
            anyUndetermined ? MatchOutcome.Indeterminate : MatchOutcome.NoMatch,
            problems);
    }
}
