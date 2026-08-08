using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Rules;
using FileOrganizer.Core.Tests.Matching;

namespace FileOrganizer.Core.Tests.Rules;

public sealed class RuleTests
{
    private static readonly EvaluationContext Context =
        new(new FixedTimeProvider(TestFiles.DefaultModified));

    // The whole include/exclude interaction in one table. Exclusion wins every
    // time it is certain, and uncertainty never turns into an action.
    [Theory]
    [InlineData(MatchOutcome.Match, MatchOutcome.Match, RuleOutcome.Excluded)]
    [InlineData(MatchOutcome.Match, MatchOutcome.NoMatch, RuleOutcome.Applies)]
    [InlineData(MatchOutcome.Match, MatchOutcome.Indeterminate, RuleOutcome.Indeterminate)]
    [InlineData(MatchOutcome.NoMatch, MatchOutcome.Match, RuleOutcome.Excluded)]
    [InlineData(MatchOutcome.NoMatch, MatchOutcome.NoMatch, RuleOutcome.NotMatched)]
    [InlineData(MatchOutcome.NoMatch, MatchOutcome.Indeterminate, RuleOutcome.NotMatched)]
    [InlineData(MatchOutcome.Indeterminate, MatchOutcome.Match, RuleOutcome.Excluded)]
    [InlineData(MatchOutcome.Indeterminate, MatchOutcome.NoMatch, RuleOutcome.Indeterminate)]
    [InlineData(MatchOutcome.Indeterminate, MatchOutcome.Indeterminate, RuleOutcome.Indeterminate)]
    public void CombinesInclusionAndExclusion(
        MatchOutcome include,
        MatchOutcome exclude,
        RuleOutcome expected)
    {
        Rule rule = new("test", StubCondition.For(include), StubCondition.For(exclude));

        Assert.Equal(expected, rule.Evaluate(TestFiles.Create(), Context).Outcome);
    }

    [Fact]
    public void AppliesWhenThereIsNothingToExclude()
    {
        Rule rule = new("archive pdfs", StubCondition.Matching());

        Assert.True(rule.Evaluate(TestFiles.Create(), Context).Applies);
    }

    [Fact]
    public void DoesNotRunInclusionOnceExclusionIsCertain()
    {
        StubCondition include = StubCondition.Matching();
        Rule rule = new("test", include, StubCondition.Matching());

        rule.Evaluate(TestFiles.Create(), Context);

        Assert.Equal(0, include.EvaluationCount);
    }

    [Fact]
    public void ADisabledRuleIsNeitherAppliedNorEvaluated()
    {
        StubCondition include = StubCondition.Matching();
        StubCondition exclude = StubCondition.NotMatching();
        Rule rule = new("test", include, exclude, isEnabled: false);

        RuleEvaluation evaluation = rule.Evaluate(TestFiles.Create(), Context);

        Assert.Equal(RuleOutcome.Disabled, evaluation.Outcome);
        Assert.False(evaluation.Applies);
        Assert.Equal(0, include.EvaluationCount);
        Assert.Equal(0, exclude.EvaluationCount);
    }

    [Fact]
    public void ADisabledRuleIsStillChecked()
    {
        // Someone editing a rule set should learn that a switched-off rule is
        // broken before they switch it on.
        Rule rule = new(
            "test",
            StubCondition.MatchingButInvalid("pattern is broken"),
            isEnabled: false);

        Assert.Equal("pattern is broken", Assert.Single(rule.Validate()).Description);
    }

    [Fact]
    public void ChecksBothHalvesOfTheRule()
    {
        Rule rule = new(
            "test",
            StubCondition.MatchingButInvalid("include is broken"),
            StubCondition.MatchingButInvalid("exclude is broken"));

        Assert.Equal(2, rule.Validate().Count());
    }

    [Fact]
    public void ReportsWhyItCouldNotBeDecided()
    {
        Rule rule = new(
            "test",
            StubCondition.Matching(),
            StubCondition.Undetermined("creation time was not reported"));

        RuleEvaluation evaluation = rule.Evaluate(TestFiles.Create(), Context);

        Assert.Equal(RuleOutcome.Indeterminate, evaluation.Outcome);
        Assert.Equal(
            "creation time was not reported",
            Assert.Single(evaluation.Problems).Description);
    }

    [Fact]
    public void GathersProblemsFromBothHalves()
    {
        Rule rule = new(
            "test",
            StubCondition.Undetermined("include could not decide"),
            StubCondition.Undetermined("exclude could not decide"));

        Assert.Equal(2, rule.Evaluate(TestFiles.Create(), Context).Problems.Count);
    }

    [Fact]
    public void NeedsANameAndSomethingToInclude()
    {
        Assert.Throws<ArgumentException>(() => new Rule("  ", StubCondition.Matching()));
        Assert.Throws<ArgumentNullException>(() => new Rule(null!, StubCondition.Matching()));
        Assert.Throws<ArgumentNullException>(() => new Rule("test", null!));
    }

    [Fact]
    public void RejectsAMissingFileOrContext()
    {
        Rule rule = new("test", StubCondition.Matching());

        Assert.Throws<ArgumentNullException>(() => rule.Evaluate(null!, Context));
        Assert.Throws<ArgumentNullException>(() => rule.Evaluate(TestFiles.Create(), null!));
    }
}
