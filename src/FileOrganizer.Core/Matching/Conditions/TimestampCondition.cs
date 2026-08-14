using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Matching.Conditions;

/// <summary>
/// Matches files by comparing one of their timestamps against a fixed instant,
/// such as "modified before 1 January 2026".
/// </summary>
/// <remarks>
/// For "older than 30 days", which moves with the clock, use
/// <see cref="AgeCondition"/>.
/// </remarks>
public sealed class TimestampCondition : Condition
{
    private readonly TimestampField _field;
    private readonly ComparisonOperator _operator;
    private readonly DateTimeOffset _threshold;

    /// <param name="field">Which timestamp to compare.</param>
    /// <param name="comparisonOperator">How to compare it.</param>
    /// <param name="threshold">
    /// The instant to compare against. Normalized to UTC, so the rule means the
    /// same moment regardless of the time zone it was written in.
    /// </param>
    public TimestampCondition(
        TimestampField field,
        ComparisonOperator comparisonOperator,
        DateTimeOffset threshold)
        : base(Describe(field, comparisonOperator))
    {
        _field = field;
        _operator = comparisonOperator;
        _threshold = threshold.ToUniversalTime();
    }

    /// <summary>Gets which file timestamp this condition examines.</summary>
    public TimestampField Field => _field;

    /// <summary>Gets how the file timestamp is compared with the threshold.</summary>
    public ComparisonOperator Operator => _operator;

    /// <summary>Gets the configured threshold normalized to UTC.</summary>
    public DateTimeOffset ThresholdUtc => _threshold;

    /// <inheritdoc />
    protected override ConditionResult EvaluateCore(FileEntry file, EvaluationContext context)
    {
        DateTimeOffset? timestamp = FileTimestamps.Read(_field, file);

        if (timestamp is null)
        {
            return ConditionResult.Undetermined(FileTimestamps.CreationTimeUnavailable);
        }

        return _operator.Accepts(timestamp.Value.CompareTo(_threshold))
            ? ConditionResult.Match
            : ConditionResult.NoMatch;
    }

    private static EvaluationProblem? Describe(
        TimestampField field,
        ComparisonOperator comparisonOperator) =>
        Enum.IsDefined(field) && Enum.IsDefined(comparisonOperator)
            ? null
            : InvalidConfiguration("This date condition uses a setting the application does not know.");
}
