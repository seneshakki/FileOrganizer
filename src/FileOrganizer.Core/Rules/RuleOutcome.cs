namespace FileOrganizer.Core.Rules;

/// <summary>
/// What a rule concluded about one file.
/// </summary>
/// <remarks>
/// "Did not match" and "an exclusion matched" are separate answers even though
/// neither leads to any action, because a user looking at a file that was left
/// alone wants to know which decision was reached.
/// </remarks>
public enum RuleOutcome
{
    /// <summary>The inclusion condition definitively did not match the file.</summary>
    NotMatched = 0,

    /// <summary>
    /// The exclusion condition matched, so the rule does not apply. Inclusion
    /// is intentionally not evaluated and may or may not have matched.
    /// </summary>
    Excluded = 1,

    /// <summary>The rule applies to the file.</summary>
    Applies = 2,

    /// <summary>
    /// The rule could not be decided. Nothing may be done to the file on the
    /// strength of it.
    /// </summary>
    Indeterminate = 3,

    /// <summary>The rule is switched off and was not considered.</summary>
    Disabled = 4,
}
