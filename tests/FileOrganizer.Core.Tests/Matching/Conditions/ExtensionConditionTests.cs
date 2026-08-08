using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;

namespace FileOrganizer.Core.Tests.Matching.Conditions;

public sealed class ExtensionConditionTests
{
    private static readonly EvaluationContext Context =
        new(new FixedTimeProvider(TestFiles.DefaultModified));

    [Theory]
    [InlineData("report.pdf", ".pdf")]
    [InlineData("report.pdf", "pdf")]
    [InlineData("report.pdf", " .PDF ")]
    [InlineData("archive.tar.gz", ".gz")]
    public void MatchesTheExtensionHoweverItWasWritten(string fileName, string configured)
    {
        ExtensionCondition condition = new([configured]);

        Assert.True(condition.Evaluate(TestFiles.Create(fileName), Context).IsMatch);
    }

    [Fact]
    public void OnlyLooksAtThePartAfterTheLastDot()
    {
        // "archive.tar.gz" has extension ".gz". Matching ".tar" would need a glob.
        ExtensionCondition condition = new([".tar"]);

        Assert.Equal(
            MatchOutcome.NoMatch,
            condition.Evaluate(TestFiles.Create("archive.tar.gz"), Context).Outcome);
    }

    [Fact]
    public void AcceptsAnyOfSeveralExtensions()
    {
        ExtensionCondition condition = new([".png", ".jpg", ".gif"]);

        Assert.True(condition.Evaluate(TestFiles.Create("holiday.jpg"), Context).IsMatch);
        Assert.False(condition.Evaluate(TestFiles.Create("holiday.bmp"), Context).IsMatch);
    }

    [Fact]
    public void TreatsCaseAsInsignificantByDefault()
    {
        ExtensionCondition condition = new([".zip"]);

        Assert.True(condition.Evaluate(TestFiles.Create("backup.ZIP"), Context).IsMatch);
    }

    [Fact]
    public void RespectsCaseWhenAskedTo()
    {
        ExtensionCondition condition = new([".zip"], CaseSensitivity.Sensitive);

        Assert.Equal(
            MatchOutcome.NoMatch,
            condition.Evaluate(TestFiles.Create("backup.ZIP"), Context).Outcome);
        Assert.True(condition.Evaluate(TestFiles.Create("backup.zip"), Context).IsMatch);
    }

    [Fact]
    public void FoldsCaseWithoutConsultingTheMachineLanguage()
    {
        // Case folding is ordinal. A culture-aware fold would treat the Turkish
        // dotless "ı" and "I" as the same letter on a Turkish system and as
        // different letters everywhere else, so the same rule would match
        // different files depending on the machine it ran on.
        ExtensionCondition condition = new([".ı"]);

        Assert.False(condition.Evaluate(TestFiles.Create("file.I"), Context).IsMatch);
        Assert.True(condition.Evaluate(TestFiles.Create("file.ı"), Context).IsMatch);
    }

    [Fact]
    public void MatchesFilesThatHaveNoExtension()
    {
        ExtensionCondition condition = new([ExtensionCondition.NoExtension]);

        Assert.True(condition.Evaluate(TestFiles.Create("README"), Context).IsMatch);
        Assert.True(condition.Evaluate(TestFiles.Create(".gitignore"), Context).IsMatch);
        Assert.False(condition.Evaluate(TestFiles.Create("README.md"), Context).IsMatch);
    }

    [Fact]
    public void DoesNotMatchAnExtensionlessFileByAccident()
    {
        ExtensionCondition condition = new([".md"]);

        Assert.Equal(
            MatchOutcome.NoMatch,
            condition.Evaluate(TestFiles.Create("README"), Context).Outcome);
    }

    [Fact]
    public void MatchesANonAsciiExtension()
    {
        ExtensionCondition condition = new([".belgé"]);

        Assert.True(condition.Evaluate(TestFiles.Create("rapor.belgé"), Context).IsMatch);
    }

    [Fact]
    public void ReportsAnEmptyExtensionList()
    {
        ExtensionCondition condition = new([]);

        AssertMisconfigured(condition, TestFiles.Create("report.pdf"));
    }

    [Fact]
    public void ReportsAWhitespaceOnlyEntryRatherThanGuessing()
    {
        // "" means "no extension"; "   " is a typo, and treating it as the same
        // thing would silently change which files the rule catches.
        ExtensionCondition condition = new(["   "]);

        AssertMisconfigured(condition, TestFiles.Create("README"));
    }

    [Fact]
    public void ReportsAnEntryThatCanNeverBeAnExtension()
    {
        ExtensionCondition condition = new([".tar.gz"]);

        AssertMisconfigured(condition, TestFiles.Create("archive.tar.gz"));
    }

    [Theory]
    [InlineData(".")]
    [InlineData(" . ")]
    public void ReportsADotRatherThanTreatingItAsAnExtension(string configured)
    {
        ExtensionCondition condition = new([configured]);

        AssertMisconfigured(condition, TestFiles.Create("report."));
    }

    [Fact]
    public void TakesAReadOnlySnapshotOfTheConfiguredExtensions()
    {
        string[] extensions = [".pdf"];
        ExtensionCondition condition = new(extensions);

        extensions[0] = ".txt";

        Assert.Equal([".pdf"], condition.Extensions);
        Assert.Equal(
            MatchOutcome.Match,
            condition.Evaluate(TestFiles.Create("report.pdf"), Context).Outcome);
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)condition.Extensions)[0] = ".txt");
    }

    [Fact]
    public void ReportsAnUnknownCaseSetting()
    {
        ExtensionCondition condition = new([".pdf"], (CaseSensitivity)42);

        AssertMisconfigured(condition, TestFiles.Create("report.pdf"));
    }

    [Fact]
    public void RejectsAMissingList()
    {
        Assert.Throws<ArgumentNullException>(() => new ExtensionCondition(null!));
        Assert.Throws<ArgumentNullException>(() => new ExtensionCondition([null!]));
    }

    private static void AssertMisconfigured(ExtensionCondition condition, FileEntry file)
    {
        Assert.False(condition.IsValid);
        Assert.Equal(
            EvaluationProblemKind.InvalidConfiguration,
            Assert.Single(condition.Validate()).Kind);
        Assert.Equal(
            MatchOutcome.Indeterminate,
            condition.Evaluate(file, Context).Outcome);
    }
}
