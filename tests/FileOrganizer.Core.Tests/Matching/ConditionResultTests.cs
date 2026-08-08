using FileOrganizer.Core.Matching;

namespace FileOrganizer.Core.Tests.Matching;

public sealed class ConditionResultTests
{
    [Fact]
    public void ADecidedResultCarriesNoProblems()
    {
        Assert.Equal(MatchOutcome.Match, ConditionResult.Match.Outcome);
        Assert.True(ConditionResult.Match.IsMatch);
        Assert.Empty(ConditionResult.Match.Problems);

        Assert.Equal(MatchOutcome.NoMatch, ConditionResult.NoMatch.Outcome);
        Assert.False(ConditionResult.NoMatch.IsMatch);
        Assert.Empty(ConditionResult.NoMatch.Problems);
    }

    [Fact]
    public void AnUndecidedResultCarriesItsReason()
    {
        EvaluationProblem problem = new(
            EvaluationProblemKind.PatternTimedOut,
            "the expression took too long");

        ConditionResult result = ConditionResult.Undetermined(problem);

        Assert.Equal(MatchOutcome.Indeterminate, result.Outcome);
        Assert.False(result.IsMatch);
        Assert.Same(problem, Assert.Single(result.Problems));
        Assert.Throws<NotSupportedException>(
            () => ((IList<EvaluationProblem>)result.Problems)[0] =
                new EvaluationProblem(EvaluationProblemKind.MetadataUnavailable, "replacement"));
    }

    [Fact]
    public void AnUndecidedResultCannotBeCreatedWithoutAReason()
    {
        Assert.Throws<ArgumentNullException>(() => ConditionResult.Undetermined(null!));
    }

    [Fact]
    public void AProblemNeedsADescriptionSomeoneCanRead()
    {
        Assert.Throws<ArgumentException>(
            () => new EvaluationProblem(EvaluationProblemKind.InvalidConfiguration, "   "));
        Assert.Throws<ArgumentNullException>(
            () => new EvaluationProblem(EvaluationProblemKind.InvalidConfiguration, null!));
    }
}
