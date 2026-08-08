namespace FileOrganizer.Core.Matching;

/// <summary>
/// What a condition concluded about one file, together with anything that
/// prevented it from concluding.
/// </summary>
public sealed class ConditionResult
{
    private static readonly IReadOnlyList<EvaluationProblem> NoProblems =
        Array.AsReadOnly(Array.Empty<EvaluationProblem>());

    private ConditionResult(MatchOutcome outcome, IReadOnlyList<EvaluationProblem> problems)
    {
        Outcome = outcome;
        Problems = problems.Count == 0
            ? NoProblems
            : Array.AsReadOnly(problems.ToArray());
    }

    /// <summary>A definite match, with nothing to report.</summary>
    public static ConditionResult Match { get; } = new(MatchOutcome.Match, NoProblems);

    /// <summary>A definite non-match, with nothing to report.</summary>
    public static ConditionResult NoMatch { get; } = new(MatchOutcome.NoMatch, NoProblems);

    /// <summary>Gets what the condition concluded.</summary>
    public MatchOutcome Outcome { get; }

    /// <summary>
    /// Gets the problems encountered while evaluating. Empty unless something
    /// went wrong; a result can carry problems and still be decided, when the
    /// undecidable part turned out not to affect the answer.
    /// </summary>
    public IReadOnlyList<EvaluationProblem> Problems { get; }

    /// <summary>Gets whether the file definitely satisfies the condition.</summary>
    public bool IsMatch => Outcome == MatchOutcome.Match;

    /// <summary>
    /// An undecided result. A reason is required: an undecidable condition that
    /// cannot say why is indistinguishable from a silent failure.
    /// </summary>
    public static ConditionResult Undetermined(EvaluationProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        return new ConditionResult(MatchOutcome.Indeterminate, [problem]);
    }

    /// <summary>
    /// Builds a result from an outcome and the problems collected along the
    /// way, reusing the shared instances when there is nothing to report.
    /// </summary>
    internal static ConditionResult Create(
        MatchOutcome outcome,
        IReadOnlyList<EvaluationProblem>? problems)
    {
        if (problems is null || problems.Count == 0)
        {
            return outcome switch
            {
                MatchOutcome.Match => Match,
                MatchOutcome.NoMatch => NoMatch,
                _ => throw new ArgumentException(
                    "An indeterminate result must carry the reason it could not be decided.",
                    nameof(problems)),
            };
        }

        return new ConditionResult(outcome, problems);
    }
}
