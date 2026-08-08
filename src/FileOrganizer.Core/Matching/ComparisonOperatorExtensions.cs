namespace FileOrganizer.Core.Matching;

internal static class ComparisonOperatorExtensions
{
    /// <summary>
    /// Applies the operator to the result of an <see cref="IComparable"/>
    /// comparison.
    /// </summary>
    /// <param name="comparison">
    /// Negative when the file's value is below the rule's value, zero when they
    /// are equal, positive when it is above.
    /// </param>
    public static bool Accepts(this ComparisonOperator comparisonOperator, int comparison) =>
        comparisonOperator switch
        {
            ComparisonOperator.LessThan => comparison < 0,
            ComparisonOperator.LessThanOrEqualTo => comparison <= 0,
            ComparisonOperator.EqualTo => comparison == 0,
            ComparisonOperator.GreaterThanOrEqualTo => comparison >= 0,
            ComparisonOperator.GreaterThan => comparison > 0,

            // Unreachable: conditions reject an unrecognised operator when they
            // are constructed, so this would mean the guard was skipped.
            _ => throw new ArgumentOutOfRangeException(nameof(comparisonOperator)),
        };
}
