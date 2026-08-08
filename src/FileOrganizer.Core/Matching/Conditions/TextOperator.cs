namespace FileOrganizer.Core.Matching.Conditions;

/// <summary>
/// How a name is compared against the text a rule specifies.
/// </summary>
public enum TextOperator
{
    /// <summary>The name is exactly the given text.</summary>
    Equals = 0,

    /// <summary>The name contains the given text somewhere.</summary>
    Contains = 1,

    /// <summary>The name begins with the given text.</summary>
    StartsWith = 2,

    /// <summary>The name ends with the given text.</summary>
    EndsWith = 3,
}
