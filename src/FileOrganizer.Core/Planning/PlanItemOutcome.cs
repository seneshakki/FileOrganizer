namespace FileOrganizer.Core.Planning;

/// <summary>
/// Describes whether one scanned file has a safe, unambiguous proposed action.
/// </summary>
public enum PlanItemOutcome
{
    /// <summary>The proposal is valid and no conflict is known in the supplied snapshot.</summary>
    Ready,

    /// <summary>No action is proposed for the file.</summary>
    Skipped,

    /// <summary>Safety could not be established, so the proposal must not run.</summary>
    Blocked,

    /// <summary>A definite destination collision prevents the proposal from running.</summary>
    Conflict,
}
