using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;

namespace FileOrganizer.Core.Tests.Matching.Conditions;

public sealed class AgeConditionTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan ThirtyDays = TimeSpan.FromDays(30);

    private static readonly EvaluationContext Context = new(new FixedTimeProvider(Now));

    [Theory]
    [InlineData(ComparisonOperator.GreaterThan, -1, true)]
    [InlineData(ComparisonOperator.GreaterThan, 0, false)]
    [InlineData(ComparisonOperator.GreaterThan, 1, false)]
    [InlineData(ComparisonOperator.GreaterThanOrEqualTo, 0, true)]
    [InlineData(ComparisonOperator.EqualTo, 0, true)]
    [InlineData(ComparisonOperator.EqualTo, -1, false)]
    [InlineData(ComparisonOperator.LessThan, 1, true)]
    [InlineData(ComparisonOperator.LessThan, 0, false)]
    [InlineData(ComparisonOperator.LessThanOrEqualTo, 0, true)]
    public void ComparesAgeAtTheBoundary(
        ComparisonOperator comparisonOperator,
        int secondsAfterTheBoundary,
        bool expected)
    {
        // A file modified exactly 30 days ago sits on the boundary; one second
        // later it is slightly younger, one second earlier slightly older.
        DateTimeOffset modified = Now - ThirtyDays + TimeSpan.FromSeconds(secondsAfterTheBoundary);

        AgeCondition condition = new(TimestampField.Modified, comparisonOperator, ThirtyDays);

        Assert.Equal(
            expected,
            condition.Evaluate(TestFiles.Create(modified: modified), Context).IsMatch);
    }

    [Fact]
    public void MeasuresAgainstTheContextRatherThanTheClock()
    {
        // Evaluated with a context stopped a year later, the same file is a year
        // older. Nothing here reads the machine clock.
        EvaluationContext later = new(new FixedTimeProvider(Now.AddYears(1)));

        AgeCondition condition =
            new(TimestampField.Modified, ComparisonOperator.GreaterThan, TimeSpan.FromDays(300));

        Assert.False(condition.Evaluate(TestFiles.Create(modified: Now), Context).IsMatch);
        Assert.True(condition.Evaluate(TestFiles.Create(modified: Now), later).IsMatch);
    }

    [Fact]
    public void TreatsAFileFromTheFutureAsYoungerThanAnythingRatherThanOlder()
    {
        // A file copied from a machine with a skewed clock has a negative age.
        // An "older than 30 days" rule must leave it alone.
        AgeCondition olderThan =
            new(TimestampField.Modified, ComparisonOperator.GreaterThan, ThirtyDays);

        Assert.False(
            olderThan.Evaluate(TestFiles.Create(modified: Now.AddDays(5)), Context).IsMatch);
    }

    [Fact]
    public void CanMeasureFromTheCreationDateWhenOneWasReported()
    {
        AgeCondition condition =
            new(TimestampField.Created, ComparisonOperator.GreaterThan, ThirtyDays);

        Assert.True(
            condition
                .Evaluate(TestFiles.Create(created: Now.AddDays(-90)), Context)
                .IsMatch);
    }

    [Fact]
    public void CannotDecideAnAgeMeasuredFromAnUnreportedCreationDate()
    {
        AgeCondition condition =
            new(TimestampField.Created, ComparisonOperator.GreaterThan, ThirtyDays);

        ConditionResult result = condition.Evaluate(TestFiles.Create(created: null), Context);

        Assert.Equal(MatchOutcome.Indeterminate, result.Outcome);
        Assert.Equal(
            EvaluationProblemKind.MetadataUnavailable,
            Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void ReportsANegativeAge()
    {
        AgeCondition condition = new(
            TimestampField.Modified,
            ComparisonOperator.GreaterThan,
            TimeSpan.FromDays(-1));

        Assert.False(condition.IsValid);
        Assert.Equal(
            EvaluationProblemKind.InvalidConfiguration,
            Assert.Single(condition.Validate()).Kind);
        Assert.Equal(
            MatchOutcome.Indeterminate,
            condition.Evaluate(TestFiles.Create(), Context).Outcome);
    }

    [Fact]
    public void AcceptsAnAgeOfZero()
    {
        AgeCondition condition =
            new(TimestampField.Modified, ComparisonOperator.GreaterThanOrEqualTo, TimeSpan.Zero);

        Assert.True(condition.IsValid);
        Assert.True(condition.Evaluate(TestFiles.Create(modified: Now), Context).IsMatch);
    }
}
