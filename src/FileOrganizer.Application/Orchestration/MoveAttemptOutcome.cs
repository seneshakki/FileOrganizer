namespace FileOrganizer.Application.Orchestration;

/// <summary>The precise result reported by one physical move attempt.</summary>
public enum MoveAttemptOutcome
{
    /// <summary>The native primitive reported success.</summary>
    Succeeded,

    /// <summary>A final source or destination revalidation prevented mutation.</summary>
    Blocked,

    /// <summary>A definite destination collision prevented mutation.</summary>
    Conflict,

    /// <summary>The native primitive reported another definite failure.</summary>
    Failed,
}
