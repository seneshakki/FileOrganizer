using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;

namespace FileOrganizer.Core.Tests.Matching.Conditions;

public sealed class NameConditionTests
{
    // Non-ASCII text is written as escapes throughout this file. These tests are
    // about which code points a name is made of, so spelling them out keeps the
    // meaning independent of how any editor happens to save the file.
    private const string TurkishName = "çalışma-notu.txt";  // çalışma-notu.txt
    private const string TurkishNeedle = "çalışma";         // çalışma

    private const string Precomposed = "café.txt";   // é as a single code point
    private const string Decomposed = "café.txt";   // e followed by U+0301

    private static readonly EvaluationContext Context =
        new(new FixedTimeProvider(TestFiles.DefaultModified));

    [Theory]
    [InlineData(TextOperator.Equals, "invoice-2026.pdf", true)]
    [InlineData(TextOperator.Equals, "invoice-2026", false)]
    [InlineData(TextOperator.Contains, "2026", true)]
    [InlineData(TextOperator.Contains, "2025", false)]
    [InlineData(TextOperator.StartsWith, "invoice", true)]
    [InlineData(TextOperator.StartsWith, "voice", false)]
    [InlineData(TextOperator.EndsWith, ".pdf", true)]
    [InlineData(TextOperator.EndsWith, "invoice", false)]
    public void ComparesTheWholeName(TextOperator textOperator, string value, bool expected)
    {
        NameCondition condition = new(NameTarget.FullName, textOperator, value);

        Assert.Equal(
            expected ? MatchOutcome.Match : MatchOutcome.NoMatch,
            condition.Evaluate(TestFiles.Create("invoice-2026.pdf"), Context).Outcome);
    }

    [Fact]
    public void CanIgnoreTheExtension()
    {
        NameCondition condition = new(
            NameTarget.WithoutExtension,
            TextOperator.Equals,
            "invoice-2026");

        Assert.True(condition.Evaluate(TestFiles.Create("invoice-2026.pdf"), Context).IsMatch);
        Assert.False(
            new NameCondition(NameTarget.FullName, TextOperator.Equals, "invoice-2026")
                .Evaluate(TestFiles.Create("invoice-2026.pdf"), Context)
                .IsMatch);
    }

    [Fact]
    public void StripsOnlyTheLastExtensionFromADottedName()
    {
        NameCondition condition = new(
            NameTarget.WithoutExtension,
            TextOperator.Equals,
            "archive.tar");

        Assert.True(condition.Evaluate(TestFiles.Create("archive.tar.gz"), Context).IsMatch);
    }

    [Fact]
    public void TreatsAnExtensionlessNameAsItsOwnStem()
    {
        NameCondition condition = new(NameTarget.WithoutExtension, TextOperator.Equals, "README");

        Assert.True(condition.Evaluate(TestFiles.Create("README"), Context).IsMatch);
    }

    [Fact]
    public void TreatsCaseAsInsignificantByDefault()
    {
        NameCondition condition = new(NameTarget.FullName, TextOperator.Contains, "INVOICE");

        Assert.True(condition.Evaluate(TestFiles.Create("invoice-2026.pdf"), Context).IsMatch);
    }

    [Fact]
    public void RespectsCaseWhenAskedTo()
    {
        NameCondition condition = new(
            NameTarget.FullName,
            TextOperator.Contains,
            "INVOICE",
            CaseSensitivity.Sensitive);

        Assert.Equal(
            MatchOutcome.NoMatch,
            condition.Evaluate(TestFiles.Create("invoice-2026.pdf"), Context).Outcome);
    }

    [Fact]
    public void MatchesANonAsciiName()
    {
        NameCondition condition = new(NameTarget.FullName, TextOperator.Contains, TurkishNeedle);

        Assert.True(condition.Evaluate(TestFiles.Create(TurkishName), Context).IsMatch);
    }

    [Fact]
    public void FoldsCaseWithoutConsultingTheMachineLanguage()
    {
        // Ordinal folding leaves the Turkish dotless "ı" and "I" as
        // different letters. A culture-aware fold would call them the same on a
        // Turkish system and different everywhere else, so the same rule would
        // match different files depending on where it ran.
        NameCondition condition = new(NameTarget.FullName, TextOperator.Contains, "ı");

        Assert.False(condition.Evaluate(TestFiles.Create("FILE.txt"), Context).IsMatch);
        Assert.True(condition.Evaluate(TestFiles.Create("fıle.txt"), Context).IsMatch);
    }

    [Fact]
    public void ComparesUnicodeWithoutNormalizingIt()
    {
        // The two spellings render identically but are different sequences.
        // Ordinal comparison says so. Normalizing first would change which files
        // an existing rule matches, so it is a decision to take deliberately
        // rather than a detail to slip in here.
        Assert.NotEqual(Precomposed, Decomposed, StringComparer.Ordinal);

        NameCondition condition = new(NameTarget.FullName, TextOperator.Equals, Precomposed);

        Assert.True(condition.Evaluate(TestFiles.Create(Precomposed), Context).IsMatch);
        Assert.False(condition.Evaluate(TestFiles.Create(Decomposed), Context).IsMatch);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ReportsMissingTextRatherThanMatchingEverything(string? value)
    {
        NameCondition condition = new(NameTarget.FullName, TextOperator.Contains, value!);

        Assert.False(condition.IsValid);
        Assert.Equal(
            EvaluationProblemKind.InvalidConfiguration,
            Assert.Single(condition.Validate()).Kind);
        Assert.Equal(
            MatchOutcome.Indeterminate,
            condition.Evaluate(TestFiles.Create("anything.txt"), Context).Outcome);
    }

    [Fact]
    public void ReportsAnUnknownOperator()
    {
        NameCondition condition = new(NameTarget.FullName, (TextOperator)77, "invoice");

        Assert.False(condition.IsValid);
        Assert.Equal(
            EvaluationProblemKind.InvalidConfiguration,
            Assert.Single(condition.Validate()).Kind);
        Assert.Equal(
            MatchOutcome.Indeterminate,
            condition.Evaluate(TestFiles.Create("invoice.pdf"), Context).Outcome);
    }
}
