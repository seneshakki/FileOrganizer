namespace FileOrganizer.Application.Execution;

/// <summary>The exact terminal state reported for one manifest operation.</summary>
public enum ExecutionItemOutcome
{
    /// <summary>The move was observed as applied.</summary>
    Applied,

    /// <summary>The move definitely did not take effect.</summary>
    NotApplied,

    /// <summary>A safety precondition blocked the operation.</summary>
    Blocked,

    /// <summary>A definite destination collision prevented the operation.</summary>
    Conflict,

    /// <summary>The physical outcome could not be established safely.</summary>
    Ambiguous,

    /// <summary>Fail-stop behavior prevented this later operation from being attempted.</summary>
    NotAttempted,
}
