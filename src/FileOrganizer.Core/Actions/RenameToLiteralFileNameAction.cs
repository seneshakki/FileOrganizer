using FileOrganizer.Core.Paths;

namespace FileOrganizer.Core.Actions;

/// <summary>Plans changing only the literal file name, within its current directory.</summary>
public sealed class RenameToLiteralFileNameAction : OrganizationAction
{
    public RenameToLiteralFileNameAction(string? fileName) => FileName = fileName ?? string.Empty;

    /// <summary>Gets the literal target name exactly as configured.</summary>
    public string FileName { get; }

    /// <inheritdoc />
    public override OrganizationActionKind Kind => OrganizationActionKind.RenameToLiteralFileName;

    /// <inheritdoc />
    public override IReadOnlyList<ActionValidationProblem> Validate(PathPolicy pathPolicy)
    {
        ArgumentNullException.ThrowIfNull(pathPolicy);

        PathProblem? problem = pathPolicy.ValidateFileName(FileName);
        return problem is null
            ? []
            : Array.AsReadOnly(
                new[]
                {
                    new ActionValidationProblem(
                        ActionValidationProblemKind.InvalidLiteralFileName,
                        problem),
                });
    }
}
