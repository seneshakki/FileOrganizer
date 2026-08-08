namespace FileOrganizer.Core.Matching;

/// <summary>
/// How a measured value is compared against the value a rule specifies.
/// </summary>
public enum ComparisonOperator
{
    /// <summary>The file's value is below the rule's value.</summary>
    LessThan = 0,

    /// <summary>The file's value is below or equal to the rule's value.</summary>
    LessThanOrEqualTo = 1,

    /// <summary>The file's value equals the rule's value.</summary>
    EqualTo = 2,

    /// <summary>The file's value is above or equal to the rule's value.</summary>
    GreaterThanOrEqualTo = 3,

    /// <summary>The file's value is above the rule's value.</summary>
    GreaterThan = 4,
}
