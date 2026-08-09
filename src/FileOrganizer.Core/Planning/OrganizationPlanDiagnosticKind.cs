namespace FileOrganizer.Core.Planning;

/// <summary>A stable plan-level condition affecting future execution eligibility.</summary>
public enum OrganizationPlanDiagnosticKind
{
    /// <summary>The source scan omitted or could not account for at least one entry.</summary>
    IncompleteScan,

    /// <summary>The source root uses path semantics different from the planner.</summary>
    SourcePathPolicyMismatch,

    /// <summary>Destination facts use path semantics different from the preparation.</summary>
    DestinationSnapshotPolicyMismatch,
}
