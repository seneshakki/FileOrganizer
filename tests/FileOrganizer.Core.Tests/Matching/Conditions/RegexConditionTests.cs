using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;
using FileOrganizer.Core.Matching.Logical;

namespace FileOrganizer.Core.Tests.Matching.Conditions;

public sealed class RegexConditionTests
{
    /// <summary>A name that makes a backtracking engine explore 2^40 paths.</summary>
    private static readonly string PathologicalName = new string('a', 40) + "!";

    private static readonly EvaluationContext Context =
        new(new FixedTimeProvider(TestFiles.DefaultModified));

    [Theory]
    [InlineData(@"^invoice-\d{4}\.pdf$", "invoice-2026.pdf", true)]
    [InlineData(@"^invoice-\d{4}\.pdf$", "invoice-20xx.pdf", false)]
    [InlineData(@"\d{4}", "invoice-2026.pdf", true)]
    [InlineData(@"^\d{4}$", "invoice-2026.pdf", false)]
    public void MatchesTheNameAgainstThePattern(string pattern, string fileName, bool expected)
    {
        RegexCondition condition = new(pattern);

        Assert.Equal(
            expected ? MatchOutcome.Match : MatchOutcome.NoMatch,
            condition.Evaluate(TestFiles.Create(fileName), Context).Outcome);
    }

    [Fact]
    public void IsNotAnchoredUnlessThePatternSaysSo()
    {
        RegexCondition condition = new("invoice");

        Assert.True(condition.Evaluate(TestFiles.Create("2026-invoice-final.pdf"), Context).IsMatch);
    }

    [Fact]
    public void CanMatchAgainstTheNameWithoutItsExtension()
    {
        RegexCondition condition = new(@"^invoice-\d{4}$", NameTarget.WithoutExtension);

        Assert.True(condition.Evaluate(TestFiles.Create("invoice-2026.pdf"), Context).IsMatch);
    }

    [Fact]
    public void TreatsCaseAsInsignificantByDefault()
    {
        RegexCondition condition = new("invoice");

        Assert.True(condition.Evaluate(TestFiles.Create("INVOICE.pdf"), Context).IsMatch);
    }

    [Fact]
    public void RespectsCaseWhenAskedTo()
    {
        RegexCondition condition = new("invoice", caseSensitivity: CaseSensitivity.Sensitive);

        Assert.Equal(
            MatchOutcome.NoMatch,
            condition.Evaluate(TestFiles.Create("INVOICE.pdf"), Context).Outcome);
    }

    [Fact]
    public void FoldsCaseWithoutConsultingTheMachineLanguage()
    {
        Assert.True(new RegexCondition("i").Evaluate(TestFiles.Create("FILE.txt"), Context).IsMatch);
        Assert.False(new RegexCondition("ı").Evaluate(TestFiles.Create("FILE.txt"), Context).IsMatch);
    }

    [Fact]
    public void RunsACatastrophicPatternInLinearTimeWhenItCan()
    {
        // The non-backtracking engine handles this pattern, so the classic
        // "(a+)+" blow-up simply does not happen and the answer is a plain
        // NoMatch rather than a time-out.
        RegexCondition condition = new(
            "^(a+)+$",
            timeout: TimeSpan.FromSeconds(2));

        ConditionResult result = condition.Evaluate(TestFiles.Create(PathologicalName), Context);

        Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void StillWorksForPatternsTheLinearEngineCannotRun()
    {
        // Lookaheads force the fallback to the backtracking engine. The pattern
        // has to keep working, not just keep from hanging.
        RegexCondition condition = new(@"(?=invoice)invoice-\d{4}");

        Assert.True(condition.IsValid);
        Assert.True(condition.Evaluate(TestFiles.Create("invoice-2026.pdf"), Context).IsMatch);

        ConditionResult noMatch = condition.Evaluate(TestFiles.Create("receipt-2026.pdf"), Context);
        Assert.Equal(MatchOutcome.NoMatch, noMatch.Outcome);
        Assert.Empty(noMatch.Problems);
    }

    [Fact]
    public void GivesUpOnAPatternThatCannotBeRunInTime()
    {
        // The lookahead forces the backtracking engine; "(a+)+$" then explores
        // exponentially many paths, so the time limit is what ends it. The
        // condition must report that, not throw it.
        RegexCondition condition = new(
            "^(?=a)(a+)+$",
            timeout: TimeSpan.FromMilliseconds(1));

        ConditionResult result = condition.Evaluate(TestFiles.Create(PathologicalName), Context);

        Assert.Equal(MatchOutcome.Indeterminate, result.Outcome);
        Assert.Equal(
            EvaluationProblemKind.PatternTimedOut,
            Assert.Single(result.Problems).Kind);

        // Nothing is wrong with the rule as written; it is only too slow here.
        Assert.True(condition.IsValid);
        Assert.Empty(condition.Validate());

        AndCondition tree = new(condition);
        Assert.True(tree.IsValid);
        Assert.Empty(tree.Validate());
    }

    [Theory]
    [InlineData("(")]
    [InlineData("[z-a]")]
    [InlineData(@"(?<")]
    public void ReportsAPatternItCannotUnderstand(string pattern)
    {
        RegexCondition condition = new(pattern);

        Assert.False(condition.IsValid);
        Assert.Equal(
            EvaluationProblemKind.InvalidConfiguration,
            Assert.Single(condition.Validate()).Kind);

        ConditionResult result = condition.Evaluate(TestFiles.Create("anything.txt"), Context);

        Assert.Equal(MatchOutcome.Indeterminate, result.Outcome);
    }

    [Fact]
    public void ABrokenPatternNeverMatchesAndNeverThrows()
    {
        RegexCondition condition = new("(");

        // Whatever it is asked, it answers the same way, without an exception
        // escaping into the caller.
        foreach (string name in (string[])["a.txt", "b.pdf", "anything"])
        {
            Assert.Equal(
                MatchOutcome.Indeterminate,
                condition.Evaluate(TestFiles.Create(name), Context).Outcome);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ReportsAMissingPattern(string? pattern)
    {
        RegexCondition condition = new(pattern!);

        Assert.False(condition.IsValid);
        Assert.Equal(
            EvaluationProblemKind.InvalidConfiguration,
            Assert.Single(condition.Validate()).Kind);
        Assert.Equal(
            MatchOutcome.Indeterminate,
            condition.Evaluate(TestFiles.Create(), Context).Outcome);
    }

    [Fact]
    public void ReportsATimeLimitThatCouldNeverBeMet()
    {
        RegexCondition condition = new("invoice", timeout: TimeSpan.Zero);

        Assert.False(condition.IsValid);
        Assert.Equal(
            EvaluationProblemKind.InvalidConfiguration,
            Assert.Single(condition.Validate()).Kind);
        Assert.Equal(
            MatchOutcome.Indeterminate,
            condition.Evaluate(TestFiles.Create(), Context).Outcome);
    }
}
