namespace FileOrganizer.Core.Rules;

/// <summary>
/// What a whole rule set concluded about one file.
/// </summary>
public enum RuleSetOutcome
{
    /// <summary>Every rule was considered and none applied.</summary>
    NoRuleApplies = 0,

    /// <summary>A rule applies, and it is the first one in order that does.</summary>
    RuleSelected = 1,

    /// <summary>
    /// A rule could not be decided, so no later rule may be acted on either:
    /// the undecidable rule might have been the one that should have won.
    /// </summary>
    Blocked = 2,
}
