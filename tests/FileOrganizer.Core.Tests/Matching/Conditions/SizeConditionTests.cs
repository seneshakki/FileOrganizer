using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;

namespace FileOrganizer.Core.Tests.Matching.Conditions;

public sealed class SizeConditionTests
{
    private const long Threshold = 1000;

    private static readonly EvaluationContext Context =
        new(new FixedTimeProvider(TestFiles.DefaultModified));

    // Every operator checked just below, exactly at, and just above the value
    // the rule specifies.
    [Theory]
    [InlineData(ComparisonOperator.LessThan, 999, true)]
    [InlineData(ComparisonOperator.LessThan, 1000, false)]
    [InlineData(ComparisonOperator.LessThan, 1001, false)]
    [InlineData(ComparisonOperator.LessThanOrEqualTo, 999, true)]
    [InlineData(ComparisonOperator.LessThanOrEqualTo, 1000, true)]
    [InlineData(ComparisonOperator.LessThanOrEqualTo, 1001, false)]
    [InlineData(ComparisonOperator.EqualTo, 999, false)]
    [InlineData(ComparisonOperator.EqualTo, 1000, true)]
    [InlineData(ComparisonOperator.EqualTo, 1001, false)]
    [InlineData(ComparisonOperator.GreaterThanOrEqualTo, 999, false)]
    [InlineData(ComparisonOperator.GreaterThanOrEqualTo, 1000, true)]
    [InlineData(ComparisonOperator.GreaterThanOrEqualTo, 1001, true)]
    [InlineData(ComparisonOperator.GreaterThan, 999, false)]
    [InlineData(ComparisonOperator.GreaterThan, 1000, false)]
    [InlineData(ComparisonOperator.GreaterThan, 1001, true)]
    public void ComparesSizeAtTheBoundary(
        ComparisonOperator comparisonOperator,
        long fileSize,
        bool expected)
    {
        SizeCondition condition = new(comparisonOperator, Threshold);

        Assert.Equal(
            expected,
            condition.Evaluate(TestFiles.Create(sizeInBytes: fileSize), Context).IsMatch);
    }

    [Fact]
    public void HandlesAnEmptyFile()
    {
        SizeCondition condition = new(ComparisonOperator.EqualTo, 0);

        Assert.True(condition.Evaluate(TestFiles.Create(sizeInBytes: 0), Context).IsMatch);
    }

    [Fact]
    public void HandlesSizesThatDoNotFitInAnInt()
    {
        SizeCondition condition = new(ComparisonOperator.GreaterThan, 8L * 1024 * 1024 * 1024);

        Assert.True(
            condition.Evaluate(TestFiles.Create(sizeInBytes: long.MaxValue), Context).IsMatch);
        Assert.False(
            condition.Evaluate(TestFiles.Create(sizeInBytes: 1024), Context).IsMatch);
    }

    [Fact]
    public void ReportsANegativeSize()
    {
        SizeCondition condition = new(ComparisonOperator.LessThan, -1);

        Assert.False(condition.IsValid);
        Assert.Equal(
            EvaluationProblemKind.InvalidConfiguration,
            Assert.Single(condition.Validate()).Kind);
        Assert.Equal(
            MatchOutcome.Indeterminate,
            condition.Evaluate(TestFiles.Create(), Context).Outcome);
    }

    [Fact]
    public void ReportsAnUnknownComparison()
    {
        SizeCondition condition = new((ComparisonOperator)99, 1000);

        Assert.False(condition.IsValid);
        Assert.Equal(
            MatchOutcome.Indeterminate,
            condition.Evaluate(TestFiles.Create(), Context).Outcome);
    }
}
