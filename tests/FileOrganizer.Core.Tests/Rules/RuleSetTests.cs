using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Rules;
using FileOrganizer.Core.Tests.Matching;

namespace FileOrganizer.Core.Tests.Rules;

public sealed class RuleSetTests
{
    private static readonly EvaluationContext Context =
        new(new FixedTimeProvider(TestFiles.DefaultModified));

    [Fact]
    public void RunsRulesFromTheLowestOrderUpwards()
    {
        RuleSet set = new(
        [
            Matching("third", order: 5),
            Matching("first", order: -10),
            Matching("second", order: 0),
        ]);

        Assert.Equal(["first", "second", "third"], set.Rules.Select(rule => rule.Name));
    }

    [Fact]
    public void KeepsTheGivenSequenceWhenOrdersAreEqual()
    {
        RuleSet set = new(
        [
            Matching("added first", order: 1),
            Matching("added second", order: 1),
            Matching("added third", order: 1),
        ]);

        Assert.Equal(
            ["added first", "added second", "added third"],
            set.Rules.Select(rule => rule.Name));
    }

    [Fact]
    public void TakesAReadOnlySnapshotOfTheRules()
    {
        Rule original = Matching("original", order: 0);
        Rule[] rules = [original];
        RuleSet set = new(rules);

        rules[0] = Matching("replacement", order: 0);

        Assert.Same(original, set.Rules[0]);
        Assert.Throws<NotSupportedException>(
            () => ((IList<Rule>)set.Rules)[0] = rules[0]);
    }

    [Fact]
    public void PicksTheFirstRuleThatApplies()
    {
        Rule winner = Matching("winner", order: 1);
        RuleSet set = new([NotMatching("skipped", order: 0), winner, Matching("later", order: 2)]);

        RuleSetEvaluation evaluation = set.Evaluate(TestFiles.Create(), Context);

        Assert.Equal(RuleSetOutcome.RuleSelected, evaluation.Outcome);
        Assert.Same(winner, evaluation.DecidingRule);
    }

    [Fact]
    public void DoesNotAskRulesAfterTheOneThatWon()
    {
        StubCondition later = StubCondition.Matching();
        RuleSet set = new(
        [
            Matching("winner", order: 0),
            new Rule("later", later, order: 1),
        ]);

        RuleSetEvaluation evaluation = set.Evaluate(TestFiles.Create(), Context);

        Assert.Equal(0, later.EvaluationCount);
        Assert.Equal(["winner"], evaluation.Evaluations.Select(e => e.Rule.Name));
    }

    [Fact]
    public void SkipsDisabledRulesAndLetsTheNextOneWin()
    {
        Rule winner = Matching("enabled", order: 1);
        RuleSet set = new(
        [
            new Rule("disabled", StubCondition.Matching(), isEnabled: false, order: 0),
            winner,
        ]);

        RuleSetEvaluation evaluation = set.Evaluate(TestFiles.Create(), Context);

        Assert.Same(winner, evaluation.DecidingRule);
        Assert.Equal(RuleOutcome.Disabled, evaluation.Evaluations[0].Outcome);
    }

    [Fact]
    public void StopsAtARuleItCannotDecide()
    {
        // The blocked rule outranks the one that would have applied. Acting on
        // the lower-priority rule could move a file the higher-priority rule
        // was about to claim, so nothing is selected.
        Rule blocked = new("cannot decide", StubCondition.Undetermined("no creation time"), order: 0);
        StubCondition later = StubCondition.Matching();
        RuleSet set = new([blocked, new Rule("would have applied", later, order: 1)]);

        RuleSetEvaluation evaluation = set.Evaluate(TestFiles.Create(), Context);

        Assert.Equal(RuleSetOutcome.Blocked, evaluation.Outcome);
        Assert.Same(blocked, evaluation.DecidingRule);
        Assert.Equal(0, later.EvaluationCount);
        Assert.Equal("no creation time", Assert.Single(evaluation.Problems).Description);
    }

    [Fact]
    public void ReportsThatNoRuleApplies()
    {
        RuleSet set = new([NotMatching("one", order: 0), NotMatching("two", order: 1)]);

        RuleSetEvaluation evaluation = set.Evaluate(TestFiles.Create(), Context);

        Assert.Equal(RuleSetOutcome.NoRuleApplies, evaluation.Outcome);
        Assert.Null(evaluation.DecidingRule);
        Assert.Equal(2, evaluation.Evaluations.Count);
        Assert.Empty(evaluation.Problems);
    }

    [Fact]
    public void AnEmptyRuleSetSimplyDoesNothing()
    {
        // Unlike an empty AND, which is a rule that cannot mean anything, a rule
        // set with no rules is an ordinary state: nothing has been configured.
        RuleSet set = new([]);

        RuleSetEvaluation evaluation = set.Evaluate(TestFiles.Create(), Context);

        Assert.Equal(RuleSetOutcome.NoRuleApplies, evaluation.Outcome);
        Assert.Empty(evaluation.Evaluations);
        Assert.Empty(set.Validate());
    }

    [Fact]
    public void GathersProblemsFromEveryRuleItAsked()
    {
        RuleSet set = new(
        [
            new Rule("first", StubCondition.NotMatching(), StubCondition.Undetermined("a"), order: 0),
            new Rule("second", StubCondition.Undetermined("b"), order: 1),
        ]);

        RuleSetEvaluation evaluation = set.Evaluate(TestFiles.Create(), Context);

        Assert.Equal(RuleSetOutcome.Blocked, evaluation.Outcome);
        Assert.Equal(["a", "b"], evaluation.Problems.Select(problem => problem.Description));
        Assert.Throws<NotSupportedException>(
            () => ((IList<RuleEvaluation>)evaluation.Evaluations)[0] = evaluation.Evaluations[1]);
        Assert.Throws<NotSupportedException>(
            () => ((IList<EvaluationProblem>)evaluation.Problems)[0] =
                new EvaluationProblem(EvaluationProblemKind.MetadataUnavailable, "replacement"));
    }

    [Fact]
    public void ChecksEveryRuleIncludingTheDisabledOnes()
    {
        RuleSet set = new(
        [
            new Rule("enabled", StubCondition.MatchingButInvalid("first is broken"), order: 0),
            new Rule(
                "disabled",
                StubCondition.MatchingButInvalid("second is broken"),
                isEnabled: false,
                order: 1),
        ]);

        Assert.Equal(
            ["first is broken", "second is broken"],
            set.Validate().Select(problem => problem.Description));
    }

    [Fact]
    public void RejectsMissingRules()
    {
        Assert.Throws<ArgumentNullException>(() => new RuleSet(null!));
        Assert.Throws<ArgumentNullException>(() => new RuleSet([null!]));
    }

    private static Rule Matching(string name, int order) =>
        new(name, StubCondition.Matching(), order: order);

    private static Rule NotMatching(string name, int order) =>
        new(name, StubCondition.NotMatching(), order: order);
}
