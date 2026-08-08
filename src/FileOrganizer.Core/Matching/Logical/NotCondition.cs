using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Matching.Logical;

/// <summary>
/// Matches when the condition it wraps does not.
/// </summary>
/// <remarks>
/// Negating something undecided leaves it undecided. It is worth being explicit
/// about that: treating an undecidable condition as false would make its
/// negation true, and a broken rule would start matching files instead of
/// refusing to act on them.
/// </remarks>
public sealed class NotCondition : Condition
{
    private readonly ICondition _condition;

    public NotCondition(ICondition condition)
        : base(null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        _condition = condition;
    }

    /// <summary>Gets the condition being negated.</summary>
    public ICondition Inner => _condition;

    /// <inheritdoc />
    public override IEnumerable<EvaluationProblem> Validate() =>
        base.Validate().Concat(_condition.Validate());

    /// <inheritdoc />
    protected override ConditionResult EvaluateCore(FileEntry file, EvaluationContext context)
    {
        ConditionResult result = _condition.Evaluate(file, context);

        MatchOutcome inverted = result.Outcome switch
        {
            MatchOutcome.Match => MatchOutcome.NoMatch,
            MatchOutcome.NoMatch => MatchOutcome.Match,
            _ => MatchOutcome.Indeterminate,
        };

        return ConditionResult.Create(inverted, result.Problems);
    }
}
