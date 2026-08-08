using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;
using FileOrganizer.Core.Matching.Logical;
using FileOrganizer.Core.Rules;

namespace FileOrganizer.Core.Tests.Rules;

/// <summary>
/// A rule set built from real conditions rather than stubs, to check that the
/// pieces compose into something a person would actually write.
/// </summary>
public sealed class RuleSetScenarioTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly EvaluationContext Context = new(new FixedTimeProvider(Now));

    private static readonly RuleSet Rules = new(
    [
        // Archive invoices older than a month, but never the drafts.
        new Rule(
            "archive old invoices",
            include: new AndCondition(
                new GlobCondition("invoice-*.pdf"),
                new AgeCondition(
                    TimestampField.Modified,
                    ComparisonOperator.GreaterThan,
                    TimeSpan.FromDays(30))),
            exclude: new NameCondition(NameTarget.FullName, TextOperator.Contains, "draft"),
            order: 10),

        // Move large videos out of the way.
        new Rule(
            "move large videos",
            include: new AndCondition(
                new ExtensionCondition([".mp4", ".mkv"]),
                new SizeCondition(ComparisonOperator.GreaterThanOrEqualTo, 100 * 1024 * 1024)),
            order: 20),
    ]);

    [Fact]
    public void TheRuleSetIsWellFormed()
    {
        Assert.Empty(Rules.Validate());
    }

    [Fact]
    public void ClaimsAnOldInvoice()
    {
        FileEntry file = TestFiles.Create("invoice-2025.pdf", modified: Now.AddDays(-90));

        RuleSetEvaluation evaluation = Rules.Evaluate(file, Context);

        Assert.Equal(RuleSetOutcome.RuleSelected, evaluation.Outcome);
        Assert.Equal("archive old invoices", evaluation.DecidingRule?.Name);
    }

    [Fact]
    public void LeavesARecentInvoiceAlone()
    {
        FileEntry file = TestFiles.Create("invoice-2026.pdf", modified: Now.AddDays(-3));

        Assert.Equal(RuleSetOutcome.NoRuleApplies, Rules.Evaluate(file, Context).Outcome);
    }

    [Fact]
    public void LeavesADraftAloneEvenWhenItIsOldEnough()
    {
        FileEntry file = TestFiles.Create("invoice-2025-draft.pdf", modified: Now.AddDays(-90));

        RuleSetEvaluation evaluation = Rules.Evaluate(file, Context);

        Assert.Equal(RuleSetOutcome.NoRuleApplies, evaluation.Outcome);
        Assert.Equal(RuleOutcome.Excluded, evaluation.Evaluations[0].Outcome);
    }

    [Fact]
    public void ClaimsALargeVideoWithTheLaterRule()
    {
        FileEntry file = TestFiles.Create("holiday.MKV", sizeInBytes: 512L * 1024 * 1024);

        RuleSetEvaluation evaluation = Rules.Evaluate(file, Context);

        Assert.Equal("move large videos", evaluation.DecidingRule?.Name);

        // The invoice rule was asked first and declined, as its order says.
        Assert.Equal(
            ["archive old invoices", "move large videos"],
            evaluation.Evaluations.Select(e => e.Rule.Name));
    }

    [Fact]
    public void LeavesASmallVideoAlone()
    {
        FileEntry file = TestFiles.Create("clip.mp4", sizeInBytes: 4 * 1024 * 1024);

        Assert.Equal(RuleSetOutcome.NoRuleApplies, Rules.Evaluate(file, Context).Outcome);
    }

    [Fact]
    public void ARuleAboutCreationDatesBlocksFilesThatCannotAnswerIt()
    {
        RuleSet withCreationRule = new(
        [
            new Rule(
                "tidy things created long ago",
                new AgeCondition(
                    TimestampField.Created,
                    ComparisonOperator.GreaterThan,
                    TimeSpan.FromDays(365)),
                order: 0),
        ]);

        RuleSetEvaluation evaluation = withCreationRule.Evaluate(
            TestFiles.Create("notes.txt", created: null),
            Context);

        Assert.Equal(RuleSetOutcome.Blocked, evaluation.Outcome);
        Assert.Equal(
            EvaluationProblemKind.MetadataUnavailable,
            Assert.Single(evaluation.Problems).Kind);

        // Nothing is wrong with the rule; it just cannot be answered here.
        Assert.Empty(withCreationRule.Validate());
    }
}
