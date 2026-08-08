using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Matching.Conditions;

/// <summary>
/// Matches files by how many bytes they hold.
/// </summary>
/// <remarks>
/// A range is expressed by combining two of these with
/// <see cref="Logical.AndCondition"/>, rather than by a separate range type.
/// </remarks>
public sealed class SizeCondition : Condition
{
    private readonly ComparisonOperator _operator;
    private readonly long _sizeInBytes;

    /// <param name="comparisonOperator">How the file's size is compared.</param>
    /// <param name="sizeInBytes">The size the rule specifies, in bytes.</param>
    public SizeCondition(ComparisonOperator comparisonOperator, long sizeInBytes)
        : base(Describe(comparisonOperator, sizeInBytes))
    {
        _operator = comparisonOperator;
        _sizeInBytes = sizeInBytes;
    }

    /// <inheritdoc />
    protected override ConditionResult EvaluateCore(FileEntry file, EvaluationContext context) =>
        _operator.Accepts(file.SizeInBytes.CompareTo(_sizeInBytes))
            ? ConditionResult.Match
            : ConditionResult.NoMatch;

    private static EvaluationProblem? Describe(ComparisonOperator comparisonOperator, long sizeInBytes)
    {
        if (!Enum.IsDefined(comparisonOperator))
        {
            return InvalidConfiguration("This size condition uses a comparison the application does not know.");
        }

        return sizeInBytes < 0
            ? InvalidConfiguration("A size condition cannot use a negative number of bytes.")
            : null;
    }
}
