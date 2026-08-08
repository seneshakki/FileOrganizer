using FileOrganizer.Core.Paths;

namespace FileOrganizer.Core.Actions;

/// <summary>A read-only organization intent. Actions in Core never execute file operations.</summary>
public abstract class OrganizationAction
{
    /// <summary>Gets the kind of intent.</summary>
    public abstract OrganizationActionKind Kind { get; }

    /// <summary>Gets whether this action is usable under an explicit path policy.</summary>
    public bool IsValid(PathPolicy pathPolicy) => Validate(pathPolicy).Count == 0;

    /// <summary>Reports every statically detectable configuration problem.</summary>
    public abstract IReadOnlyList<ActionValidationProblem> Validate(PathPolicy pathPolicy);

    protected static IReadOnlyList<ActionValidationProblem> ValidateDestinationDirectory(
        string destinationDirectory,
        PathPolicy pathPolicy)
    {
        ArgumentNullException.ThrowIfNull(pathPolicy);

        PathResolution resolution = pathPolicy.CanonicalizeAbsolute(destinationDirectory);
        return resolution.Succeeded
            ? []
            : Array.AsReadOnly(
                new[]
                {
                    new ActionValidationProblem(
                        ActionValidationProblemKind.InvalidDestinationDirectory,
                        resolution.Problem!),
                });
    }
}
