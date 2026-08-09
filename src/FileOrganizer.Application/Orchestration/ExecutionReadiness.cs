namespace FileOrganizer.Application.Orchestration;

/// <summary>The result of a read-only execution precondition check.</summary>
public enum ExecutionReadiness
{
    /// <summary>The operation may proceed to the next safety boundary.</summary>
    Ready,

    /// <summary>A source or destination safety precondition is not satisfied.</summary>
    Blocked,

    /// <summary>A definite destination collision prevents the operation.</summary>
    Conflict,
}
