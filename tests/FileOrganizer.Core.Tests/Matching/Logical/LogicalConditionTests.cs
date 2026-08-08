using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;
using FileOrganizer.Core.Matching.Logical;

namespace FileOrganizer.Core.Tests.Matching.Logical;

public sealed class LogicalConditionTests
{
    private static readonly FileEntry AnyFile = TestFiles.Create();

    private static readonly EvaluationContext Context =
        new(new FixedTimeProvider(TestFiles.DefaultModified));

    // A definite non-match settles a conjunction; a match needs every part
    // decided.
    [Theory]
    [InlineData(MatchOutcome.Match, MatchOutcome.Match, MatchOutcome.Match)]
    [InlineData(MatchOutcome.Match, MatchOutcome.NoMatch, MatchOutcome.NoMatch)]
    [InlineData(MatchOutcome.Match, MatchOutcome.Indeterminate, MatchOutcome.Indeterminate)]
    [InlineData(MatchOutcome.NoMatch, MatchOutcome.Match, MatchOutcome.NoMatch)]
    [InlineData(MatchOutcome.NoMatch, MatchOutcome.NoMatch, MatchOutcome.NoMatch)]
    [InlineData(MatchOutcome.NoMatch, MatchOutcome.Indeterminate, MatchOutcome.NoMatch)]
    [InlineData(MatchOutcome.Indeterminate, MatchOutcome.Match, MatchOutcome.Indeterminate)]
    [InlineData(MatchOutcome.Indeterminate, MatchOutcome.NoMatch, MatchOutcome.NoMatch)]
    [InlineData(MatchOutcome.Indeterminate, MatchOutcome.Indeterminate, MatchOutcome.Indeterminate)]
    public void AndCombinesThreeAnswers(MatchOutcome left, MatchOutcome right, MatchOutcome expected)
    {
        AndCondition condition = new(StubCondition.For(left), StubCondition.For(right));

        Assert.Equal(expected, condition.Evaluate(AnyFile, Context).Outcome);
    }

    // A definite match settles a disjunction; concluding nothing matched needs
    // every part decided.
    [Theory]
    [InlineData(MatchOutcome.Match, MatchOutcome.Match, MatchOutcome.Match)]
    [InlineData(MatchOutcome.Match, MatchOutcome.NoMatch, MatchOutcome.Match)]
    [InlineData(MatchOutcome.Match, MatchOutcome.Indeterminate, MatchOutcome.Match)]
    [InlineData(MatchOutcome.NoMatch, MatchOutcome.Match, MatchOutcome.Match)]
    [InlineData(MatchOutcome.NoMatch, MatchOutcome.NoMatch, MatchOutcome.NoMatch)]
    [InlineData(MatchOutcome.NoMatch, MatchOutcome.Indeterminate, MatchOutcome.Indeterminate)]
    [InlineData(MatchOutcome.Indeterminate, MatchOutcome.Match, MatchOutcome.Match)]
    [InlineData(MatchOutcome.Indeterminate, MatchOutcome.NoMatch, MatchOutcome.Indeterminate)]
    [InlineData(MatchOutcome.Indeterminate, MatchOutcome.Indeterminate, MatchOutcome.Indeterminate)]
    public void OrCombinesThreeAnswers(MatchOutcome left, MatchOutcome right, MatchOutcome expected)
    {
        OrCondition condition = new(StubCondition.For(left), StubCondition.For(right));

        Assert.Equal(expected, condition.Evaluate(AnyFile, Context).Outcome);
    }

    [Theory]
    [InlineData(MatchOutcome.Match, MatchOutcome.NoMatch)]
    [InlineData(MatchOutcome.NoMatch, MatchOutcome.Match)]
    [InlineData(MatchOutcome.Indeterminate, MatchOutcome.Indeterminate)]
    public void NotInvertsOnlyWhatWasDecided(MatchOutcome inner, MatchOutcome expected)
    {
        NotCondition condition = new(StubCondition.For(inner));

        Assert.Equal(expected, condition.Evaluate(AnyFile, Context).Outcome);
    }

    [Fact]
    public void AndStopsAtTheFirstDefiniteNonMatch()
    {
        StubCondition later = StubCondition.Matching();
        AndCondition condition = new(StubCondition.NotMatching(), later);

        condition.Evaluate(AnyFile, Context);

        Assert.Equal(0, later.EvaluationCount);
    }

    [Fact]
    public void OrStopsAtTheFirstDefiniteMatch()
    {
        StubCondition later = StubCondition.NotMatching();
        OrCondition condition = new(StubCondition.Matching(), later);

        condition.Evaluate(AnyFile, Context);

        Assert.Equal(0, later.EvaluationCount);
    }

    [Fact]
    public void AndReportsWhyItCouldNotDecide()
    {
        AndCondition condition = new(
            StubCondition.Matching(),
            StubCondition.Undetermined("the file system reported no creation time"));

        ConditionResult result = condition.Evaluate(AnyFile, Context);

        Assert.Equal(MatchOutcome.Indeterminate, result.Outcome);
        EvaluationProblem problem = Assert.Single(result.Problems);
        Assert.Equal("the file system reported no creation time", problem.Description);
    }

    [Fact]
    public void AndKeepsProblemsSeenBeforeItSettled()
    {
        // The answer is definitely NoMatch, but the undecidable condition that
        // came first is still worth surfacing.
        AndCondition condition = new(
            StubCondition.Undetermined("could not read metadata"),
            StubCondition.NotMatching());

        ConditionResult result = condition.Evaluate(AnyFile, Context);

        Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void NotKeepsTheProblemOfWhatItNegated()
    {
        NotCondition condition = new(StubCondition.Undetermined("could not decide"));

        ConditionResult result = condition.Evaluate(AnyFile, Context);

        Assert.Equal(MatchOutcome.Indeterminate, result.Outcome);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void NestedCompositionEvaluatesAsWritten()
    {
        // (match OR no-match) AND NOT(no-match)  ->  match
        AndCondition condition = new(
            new OrCondition(StubCondition.Matching(), StubCondition.NotMatching()),
            new NotCondition(StubCondition.NotMatching()));

        Assert.True(condition.Evaluate(AnyFile, Context).IsMatch);
    }

    [Fact]
    public void AnEmptyAndIsUndecidableRatherThanVacuouslyTrue()
    {
        AndCondition condition = new();

        ConditionResult result = condition.Evaluate(AnyFile, Context);

        Assert.Equal(MatchOutcome.Indeterminate, result.Outcome);
        Assert.Equal(
            EvaluationProblemKind.InvalidConfiguration,
            Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void AnEmptyOrIsUndecidableRatherThanVacuouslyFalse()
    {
        OrCondition condition = new();

        Assert.Equal(MatchOutcome.Indeterminate, condition.Evaluate(AnyFile, Context).Outcome);
        Assert.Single(condition.Validate());
    }

    [Fact]
    public void ValidityReachesEveryNestedCondition()
    {
        RegexCondition invalidRegex = new("(");
        NotCondition not = new(invalidRegex);
        OrCondition or = new(
            StubCondition.Matching(),
            not);
        AndCondition and = new(
            StubCondition.Matching(),
            or);

        foreach (Condition condition in (Condition[])[invalidRegex, not, or, and])
        {
            Assert.False(condition.IsValid);

            EvaluationProblem problem = Assert.Single(condition.Validate());
            Assert.Equal(EvaluationProblemKind.InvalidConfiguration, problem.Kind);
        }
    }

    [Fact]
    public void ValidateFindsNothingWrongWithAUsableCondition()
    {
        AndCondition condition = new(StubCondition.Matching(), StubCondition.NotMatching());

        Assert.True(condition.IsValid);
        Assert.Empty(condition.Validate());
    }

    [Fact]
    public void TakesAReadOnlySnapshotOfItsChildren()
    {
        StubCondition original = StubCondition.Matching();
        ICondition[] children = [original];
        AndCondition condition = new(children);

        children[0] = StubCondition.NotMatching();

        Assert.Same(original, condition.Children[0]);
        Assert.Equal(MatchOutcome.Match, condition.Evaluate(AnyFile, Context).Outcome);
        Assert.Throws<NotSupportedException>(
            () => ((IList<ICondition>)condition.Children)[0] = StubCondition.NotMatching());
    }

    [Fact]
    public void RejectsANullCondition()
    {
        Assert.Throws<ArgumentNullException>(() => new NotCondition(null!));
        Assert.Throws<ArgumentNullException>(() => new AndCondition(null!));
        Assert.Throws<ArgumentNullException>(() => new AndCondition(StubCondition.Matching(), null!));
    }

    [Fact]
    public void RejectsANullFileOrContext()
    {
        AndCondition condition = new(StubCondition.Matching());

        Assert.Throws<ArgumentNullException>(() => condition.Evaluate(null!, Context));
        Assert.Throws<ArgumentNullException>(() => condition.Evaluate(AnyFile, null!));
    }
}
