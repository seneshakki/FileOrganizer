using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.Core.Planning;

/// <summary>A structured reason attached to one immutable plan item.</summary>
public sealed record PlanReason
{
    /// <param name="kind">The stable category used for programmatic decisions.</param>
    /// <param name="description">A concise explanation suitable for a dry-run preview.</param>
    public PlanReason(
        PlanReasonKind kind,
        string description,
        ActionValidationProblem? actionProblem = null,
        PathProblem? pathProblem = null,
        DestinationInspectionFailure? destinationInspectionFailure = null,
        CanonicalPath? affectedPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        Kind = kind;
        Description = description;
        ActionProblem = actionProblem;
        PathProblem = pathProblem;
        DestinationInspectionFailure = destinationInspectionFailure;
        AffectedPath = affectedPath;
    }

    /// <summary>Gets the stable category of the reason.</summary>
    public PlanReasonKind Kind { get; }

    /// <summary>Gets the explanation suitable for display.</summary>
    public string Description { get; }

    /// <summary>Gets the exact static action problem, when this reason represents one.</summary>
    public ActionValidationProblem? ActionProblem { get; }

    /// <summary>Gets the exact lexical path problem, when path resolution failed.</summary>
    public PathProblem? PathProblem { get; }

    /// <summary>Gets the exact destination inspection failure, when inspection failed.</summary>
    public DestinationInspectionFailure? DestinationInspectionFailure { get; }

    /// <summary>Gets the canonical path this reason concerns, when applicable.</summary>
    public CanonicalPath? AffectedPath { get; }
}
