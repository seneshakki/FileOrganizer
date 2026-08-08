using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching;

namespace FileOrganizer.Core.Tests.Matching;

/// <summary>
/// A condition that returns a fixed answer and counts how often it was asked,
/// so that composition can be tested without dragging in a real matcher.
/// </summary>
internal sealed class StubCondition : ICondition
{
    private readonly ConditionResult _result;
    private readonly EvaluationProblem[] _staticProblems;

    private StubCondition(ConditionResult result, EvaluationProblem[] staticProblems)
    {
        _result = result;
        _staticProblems = staticProblems;
    }

    /// <summary>Gets how many times this condition has been evaluated.</summary>
    public int EvaluationCount { get; private set; }

    public static StubCondition Matching() => new(ConditionResult.Match, []);

    public static StubCondition NotMatching() => new(ConditionResult.NoMatch, []);

    public static StubCondition Undetermined(string description = "stub cannot decide") =>
        new(
            ConditionResult.Undetermined(
                new EvaluationProblem(EvaluationProblemKind.MetadataUnavailable, description)),
            []);

    /// <summary>A condition that matches but reports a problem when validated.</summary>
    public static StubCondition MatchingButInvalid(string description = "stub is misconfigured") =>
        new(
            ConditionResult.Match,
            [new EvaluationProblem(EvaluationProblemKind.InvalidConfiguration, description)]);

    public static StubCondition For(MatchOutcome outcome) => outcome switch
    {
        MatchOutcome.Match => Matching(),
        MatchOutcome.NoMatch => NotMatching(),
        MatchOutcome.Indeterminate => Undetermined(),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    public ConditionResult Evaluate(FileEntry file, EvaluationContext context)
    {
        EvaluationCount++;
        return _result;
    }

    public IEnumerable<EvaluationProblem> Validate() => _staticProblems;
}
