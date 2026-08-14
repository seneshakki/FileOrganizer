using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Matching.Conditions;

namespace FileOrganizer.Core.Tests.Matching.Conditions;

public sealed class ConditionConfigurationTests
{
    [Fact]
    public void LeafConditionsExposeTheImmutableConfigurationUsedForEvaluation()
    {
        ExtensionCondition extension = new(["JPG"], CaseSensitivity.Sensitive);
        NameCondition name = new(
            NameTarget.WithoutExtension,
            TextOperator.EndsWith,
            "final",
            CaseSensitivity.Sensitive);
        GlobCondition glob = new(
            "report-*.pdf",
            NameTarget.FullName,
            CaseSensitivity.Insensitive);
        TimeSpan regexTimeout = TimeSpan.FromMilliseconds(250);
        RegexCondition regex = new(
            "^invoice",
            NameTarget.WithoutExtension,
            CaseSensitivity.Sensitive,
            regexTimeout);
        SizeCondition size = new(ComparisonOperator.GreaterThan, 1_024);
        DateTimeOffset configuredThreshold = new(
            2026,
            4,
            3,
            2,
            1,
            0,
            TimeSpan.FromHours(3));
        TimestampCondition timestamp = new(
            TimestampField.Created,
            ComparisonOperator.LessThanOrEqualTo,
            configuredThreshold);
        TimeSpan configuredAge = TimeSpan.FromDays(30);
        AgeCondition age = new(
            TimestampField.Modified,
            ComparisonOperator.GreaterThanOrEqualTo,
            configuredAge);

        Assert.Equal([".JPG"], extension.Extensions);
        Assert.Equal(CaseSensitivity.Sensitive, extension.CaseSensitivity);

        Assert.Equal(NameTarget.WithoutExtension, name.Target);
        Assert.Equal(TextOperator.EndsWith, name.Operator);
        Assert.Equal("final", name.Value);
        Assert.Equal(CaseSensitivity.Sensitive, name.CaseSensitivity);

        Assert.Equal("report-*.pdf", glob.Pattern);
        Assert.Equal(NameTarget.FullName, glob.Target);
        Assert.Equal(CaseSensitivity.Insensitive, glob.CaseSensitivity);

        Assert.Equal("^invoice", regex.Pattern);
        Assert.Equal(NameTarget.WithoutExtension, regex.Target);
        Assert.Equal(CaseSensitivity.Sensitive, regex.CaseSensitivity);
        Assert.Equal(regexTimeout, regex.Timeout);
        Assert.Equal(RegexCondition.DefaultTimeout, new RegexCondition("invoice").Timeout);

        Assert.Equal(ComparisonOperator.GreaterThan, size.Operator);
        Assert.Equal(1_024, size.SizeInBytes);

        Assert.Equal(TimestampField.Created, timestamp.Field);
        Assert.Equal(ComparisonOperator.LessThanOrEqualTo, timestamp.Operator);
        Assert.Equal(configuredThreshold.ToUniversalTime(), timestamp.ThresholdUtc);
        Assert.Equal(TimeSpan.Zero, timestamp.ThresholdUtc.Offset);

        Assert.Equal(TimestampField.Modified, age.Field);
        Assert.Equal(ComparisonOperator.GreaterThanOrEqualTo, age.Operator);
        Assert.Equal(configuredAge, age.Age);
    }

    [Fact]
    public void InvalidRegexStillRetainsItsConfiguredValuesForDiagnostics()
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(25);
        RegexCondition condition = new(
            "(",
            NameTarget.WithoutExtension,
            CaseSensitivity.Sensitive,
            timeout);

        Assert.False(condition.IsValid);
        Assert.Equal("(", condition.Pattern);
        Assert.Equal(NameTarget.WithoutExtension, condition.Target);
        Assert.Equal(CaseSensitivity.Sensitive, condition.CaseSensitivity);
        Assert.Equal(timeout, condition.Timeout);
    }
}
