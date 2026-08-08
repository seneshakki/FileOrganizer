using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Matching.Conditions;

/// <summary>
/// Matches files by how long ago one of their timestamps was, such as
/// "modified more than 30 days ago".
/// </summary>
/// <remarks>
/// The age is measured against the instant held by the
/// <see cref="EvaluationContext"/>, not against the clock, so every file in a
/// run is judged against the same moment.
///
/// A file whose timestamp lies in the future — a copy from a machine with a
/// skewed clock, say — has a negative age. That compares perfectly well, and it
/// means an "older than" rule correctly leaves such a file alone rather than
/// treating it as ancient.
/// </remarks>
public sealed class AgeCondition : Condition
{
    private readonly TimestampField _field;
    private readonly ComparisonOperator _operator;
    private readonly TimeSpan _age;

    /// <param name="field">Which timestamp to measure from.</param>
    /// <param name="comparisonOperator">
    /// How the file's age is compared. "Older than" is
    /// <see cref="ComparisonOperator.GreaterThan"/>, because an older file has a
    /// larger age.
    /// </param>
    /// <param name="age">The age the rule specifies.</param>
    public AgeCondition(
        TimestampField field,
        ComparisonOperator comparisonOperator,
        TimeSpan age)
        : base(Describe(field, comparisonOperator, age))
    {
        _field = field;
        _operator = comparisonOperator;
        _age = age;
    }

    /// <inheritdoc />
    protected override ConditionResult EvaluateCore(FileEntry file, EvaluationContext context)
    {
        DateTimeOffset? timestamp = FileTimestamps.Read(_field, file);

        if (timestamp is null)
        {
            return ConditionResult.Undetermined(FileTimestamps.CreationTimeUnavailable);
        }

        TimeSpan age = context.UtcNow - timestamp.Value;

        return _operator.Accepts(age.CompareTo(_age))
            ? ConditionResult.Match
            : ConditionResult.NoMatch;
    }

    private static EvaluationProblem? Describe(
        TimestampField field,
        ComparisonOperator comparisonOperator,
        TimeSpan age)
    {
        if (!Enum.IsDefined(field) || !Enum.IsDefined(comparisonOperator))
        {
            return InvalidConfiguration("This age condition uses a setting the application does not know.");
        }

        return age < TimeSpan.Zero
            ? InvalidConfiguration("An age condition cannot use a negative age.")
            : null;
    }
}
