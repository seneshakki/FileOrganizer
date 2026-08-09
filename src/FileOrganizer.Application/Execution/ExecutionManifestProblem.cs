using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.Application.Execution;

/// <summary>A structured manifest compilation rejection.</summary>
public sealed record ExecutionManifestProblem
{
    public ExecutionManifestProblem(
        ExecutionManifestProblemKind kind,
        string description,
        CanonicalPath? affectedSource = null,
        OrganizationActionKind? actionKind = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (actionKind is not null && !Enum.IsDefined(actionKind.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(actionKind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        Kind = kind;
        Description = description;
        AffectedSource = affectedSource;
        ActionKind = actionKind;
    }

    /// <summary>Gets the stable rejection category.</summary>
    public ExecutionManifestProblemKind Kind { get; }

    /// <summary>Gets a concise audit description.</summary>
    public string Description { get; }

    /// <summary>Gets the ready source that requested an unsupported action, if applicable.</summary>
    public CanonicalPath? AffectedSource { get; }

    /// <summary>Gets the unsupported action kind, if applicable.</summary>
    public OrganizationActionKind? ActionKind { get; }
}
