namespace FileOrganizer.Core.Matching;

/// <summary>
/// The answer a condition gives about one file.
/// </summary>
/// <remarks>
/// There are three answers rather than two because a condition can fail to
/// reach one. Folding that case into <see cref="NoMatch"/> would hide a broken
/// rule behind behaviour indistinguishable from a rule that simply did not
/// apply, and the user would have no way to find out why nothing happened.
/// </remarks>
public enum MatchOutcome
{
    /// <summary>The file definitely does not satisfy the condition.</summary>
    NoMatch = 0,

    /// <summary>The file definitely satisfies the condition.</summary>
    Match = 1,

    /// <summary>
    /// The condition could not be decided, because it is misconfigured or
    /// because the file metadata it needs was never reported. Nothing may be
    /// done to the file on this basis.
    /// </summary>
    Indeterminate = 2,
}
