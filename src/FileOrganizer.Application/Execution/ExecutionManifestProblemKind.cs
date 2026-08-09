namespace FileOrganizer.Application.Execution;

/// <summary>A stable reason why a dry-run plan cannot become an execution manifest.</summary>
public enum ExecutionManifestProblemKind
{
    /// <summary>The dry-run plan did not satisfy its top-level execution gate.</summary>
    PlanNotEligible,

    /// <summary>A ready item requested an action outside M3B-1's move-only scope.</summary>
    UnsupportedReadyAction,

    /// <summary>A ready item violated an execution safety invariant.</summary>
    InvalidReadyItem,
}
