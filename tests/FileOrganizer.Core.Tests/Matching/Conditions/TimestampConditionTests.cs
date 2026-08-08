using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;

namespace FileOrganizer.Core.Tests.Matching.Conditions;

public sealed class TimestampConditionTests
{
    private static readonly DateTimeOffset Threshold =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly EvaluationContext Context =
        new(new FixedTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)));

    [Theory]
    [InlineData(ComparisonOperator.LessThan, -1, true)]
    [InlineData(ComparisonOperator.LessThan, 0, false)]
    [InlineData(ComparisonOperator.LessThan, 1, false)]
    [InlineData(ComparisonOperator.LessThanOrEqualTo, 0, true)]
    [InlineData(ComparisonOperator.EqualTo, -1, false)]
    [InlineData(ComparisonOperator.EqualTo, 0, true)]
    [InlineData(ComparisonOperator.GreaterThanOrEqualTo, 0, true)]
    [InlineData(ComparisonOperator.GreaterThan, 0, false)]
    [InlineData(ComparisonOperator.GreaterThan, 1, true)]
    public void ComparesTheModificationDateAtTheBoundary(
        ComparisonOperator comparisonOperator,
        int offsetInSeconds,
        bool expected)
    {
        TimestampCondition condition =
            new(TimestampField.Modified, comparisonOperator, Threshold);

        Assert.Equal(
            expected ? MatchOutcome.Match : MatchOutcome.NoMatch,
            condition
                .Evaluate(
                    TestFiles.Create(modified: Threshold.AddSeconds(offsetInSeconds)),
                    Context)
                .Outcome);
    }

    [Fact]
    public void ComparesInstantsRatherThanWallClockReadings()
    {
        // 02:00 at +03:00 is 23:00 the previous day in UTC, and therefore before
        // the threshold. A rule written in one time zone has to mean the same
        // moment when it runs in another.
        DateTimeOffset modified = new(2026, 1, 1, 2, 0, 0, TimeSpan.FromHours(3));

        TimestampCondition condition =
            new(TimestampField.Modified, ComparisonOperator.LessThan, Threshold);

        Assert.True(condition.Evaluate(TestFiles.Create(modified: modified), Context).IsMatch);
    }

    [Fact]
    public void ComparesACreationDateWhenOneWasReported()
    {
        TimestampCondition condition =
            new(TimestampField.Created, ComparisonOperator.GreaterThan, Threshold);

        Assert.True(
            condition
                .Evaluate(TestFiles.Create(created: Threshold.AddDays(1)), Context)
                .IsMatch);
    }

    [Fact]
    public void CannotDecideACreationDateThatWasNeverReported()
    {
        TimestampCondition condition =
            new(TimestampField.Created, ComparisonOperator.GreaterThan, Threshold);

        ConditionResult result = condition.Evaluate(TestFiles.Create(created: null), Context);

        Assert.Equal(MatchOutcome.Indeterminate, result.Outcome);
        Assert.Equal(
            EvaluationProblemKind.MetadataUnavailable,
            Assert.Single(result.Problems).Kind);

        // The condition itself is fine; there is nothing for the user to fix.
        Assert.True(condition.IsValid);
        Assert.Empty(condition.Validate());
    }

    [Fact]
    public void ADateRuleAboutModificationIsNeverUndecidable()
    {
        TimestampCondition condition =
            new(TimestampField.Modified, ComparisonOperator.GreaterThan, Threshold);

        Assert.NotEqual(
            MatchOutcome.Indeterminate,
            condition.Evaluate(TestFiles.Create(created: null), Context).Outcome);
    }

    [Fact]
    public void ReportsAnUnknownFieldOrComparison()
    {
        TimestampCondition unknownField =
            new((TimestampField)9, ComparisonOperator.LessThan, Threshold);
        TimestampCondition unknownComparison =
            new(TimestampField.Modified, (ComparisonOperator)9, Threshold);

        Assert.False(unknownField.IsValid);
        Assert.False(unknownComparison.IsValid);
        Assert.All(
            new[] { unknownField, unknownComparison },
            condition => Assert.Equal(
                EvaluationProblemKind.InvalidConfiguration,
                Assert.Single(condition.Validate()).Kind));
        Assert.Equal(
            MatchOutcome.Indeterminate,
            unknownField.Evaluate(TestFiles.Create(), Context).Outcome);
        Assert.Equal(
            MatchOutcome.Indeterminate,
            unknownComparison.Evaluate(TestFiles.Create(), Context).Outcome);
    }
}
