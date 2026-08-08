using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;

namespace FileOrganizer.Core.Tests.Matching.Conditions;

/// <summary>
/// These tests pin down the pattern language rather than assume it. The
/// behaviour they describe was measured against the framework matcher before
/// being written down here.
/// </summary>
public sealed class GlobConditionTests
{
    private static readonly EvaluationContext Context =
        new(new FixedTimeProvider(TestFiles.DefaultModified));

    [Theory]
    [InlineData("*.txt", "notes.txt", true)]
    [InlineData("*.txt", "notes.md", false)]
    [InlineData("*.txt", "archive.tar.txt", true)]
    [InlineData("*.txt", ".txt", true)]
    [InlineData("invoice-*", "invoice-2026.pdf", true)]
    [InlineData("invoice-*", "invoice-", true)]
    [InlineData("invoice-*", "receipt-2026.pdf", false)]
    [InlineData("*", "anything-at-all", true)]
    public void StarStandsForAnyRunOfCharacters(string pattern, string fileName, bool expected)
    {
        GlobCondition condition = new(pattern);

        Assert.Equal(
            expected ? MatchOutcome.Match : MatchOutcome.NoMatch,
            condition.Evaluate(TestFiles.Create(fileName), Context).Outcome);
    }

    [Theory]
    [InlineData("?.txt", "a.txt", true)]
    [InlineData("?.txt", "ab.txt", false)]
    [InlineData("?.txt", ".txt", false)]
    [InlineData("log-??.txt", "log-01.txt", true)]
    [InlineData("log-??.txt", "log-1.txt", false)]
    public void QuestionMarkStandsForExactlyOneCharacter(
        string pattern,
        string fileName,
        bool expected)
    {
        GlobCondition condition = new(pattern);

        Assert.Equal(
            expected ? MatchOutcome.Match : MatchOutcome.NoMatch,
            condition.Evaluate(TestFiles.Create(fileName), Context).Outcome);
    }

    [Fact]
    public void RequiresADotWhereThePatternHasOne()
    {
        GlobCondition condition = new("*.*");

        Assert.False(condition.Evaluate(TestFiles.Create("README"), Context).IsMatch);
        Assert.True(condition.Evaluate(TestFiles.Create("README.md"), Context).IsMatch);
    }

    [Fact]
    public void TreatsBracketsAsLiteralCharactersRatherThanACharacterClass()
    {
        // There are no character classes in this pattern language. A rule author
        // who writes [ab] gets files literally named that, not a or b.
        GlobCondition condition = new("[ab].txt");

        Assert.False(condition.Evaluate(TestFiles.Create("a.txt"), Context).IsMatch);
        Assert.True(condition.Evaluate(TestFiles.Create("[ab].txt"), Context).IsMatch);
    }

    [Theory]
    [InlineData("file{1}.txt")]
    [InlineData("a+b.txt")]
    [InlineData("100%-done.txt")]
    public void TreatsRegularExpressionSyntaxAsLiteralText(string fileName)
    {
        GlobCondition condition = new(fileName);

        Assert.True(condition.Evaluate(TestFiles.Create(fileName), Context).IsMatch);
    }

    [Fact]
    public void TreatsCaseAsInsignificantByDefault()
    {
        GlobCondition condition = new("*.txt");

        Assert.True(condition.Evaluate(TestFiles.Create("NOTES.TXT"), Context).IsMatch);
    }

    [Fact]
    public void RespectsCaseWhenAskedTo()
    {
        GlobCondition condition = new("*.txt", caseSensitivity: CaseSensitivity.Sensitive);

        Assert.Equal(
            MatchOutcome.NoMatch,
            condition.Evaluate(TestFiles.Create("NOTES.TXT"), Context).Outcome);
        Assert.True(condition.Evaluate(TestFiles.Create("notes.txt"), Context).IsMatch);
    }

    [Fact]
    public void FoldsCaseWithoutConsultingTheMachineLanguage()
    {
        Assert.True(new GlobCondition("*i*").Evaluate(TestFiles.Create("FILE.txt"), Context).IsMatch);
        Assert.False(new GlobCondition("*ı*").Evaluate(TestFiles.Create("FILE.txt"), Context).IsMatch);
        Assert.True(new GlobCondition("*ı*").Evaluate(TestFiles.Create("fıle.txt"), Context).IsMatch);
    }

    [Fact]
    public void CanMatchAgainstTheNameWithoutItsExtension()
    {
        GlobCondition condition = new("invoice-????", NameTarget.WithoutExtension);

        Assert.True(condition.Evaluate(TestFiles.Create("invoice-2026.pdf"), Context).IsMatch);
        Assert.False(
            new GlobCondition("invoice-????").Evaluate(TestFiles.Create("invoice-2026.pdf"), Context).IsMatch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ReportsAMissingPatternRatherThanMatchingNothingForever(string? pattern)
    {
        GlobCondition condition = new(pattern!);

        Assert.False(condition.IsValid);
        Assert.Equal(
            EvaluationProblemKind.InvalidConfiguration,
            Assert.Single(condition.Validate()).Kind);
        Assert.Equal(
            MatchOutcome.Indeterminate,
            condition.Evaluate(TestFiles.Create("anything.txt"), Context).Outcome);
    }

    [Fact]
    public void ReportsAnUnknownSetting()
    {
        GlobCondition condition = new("*.pdf", (NameTarget)5);

        Assert.False(condition.IsValid);
        Assert.Equal(
            EvaluationProblemKind.InvalidConfiguration,
            Assert.Single(condition.Validate()).Kind);
        Assert.Equal(
            MatchOutcome.Indeterminate,
            condition.Evaluate(TestFiles.Create("report.pdf"), Context).Outcome);
    }
}
