using FileOrganizer.Core.Paths;

namespace FileOrganizer.Core.Actions;

/// <summary>Plans copying a file into one configured destination directory.</summary>
public sealed class CopyToDirectoryAction : OrganizationAction
{
    public CopyToDirectoryAction(string? destinationDirectory) =>
        DestinationDirectory = destinationDirectory ?? string.Empty;

    /// <summary>Gets the destination exactly as configured.</summary>
    public string DestinationDirectory { get; }

    /// <inheritdoc />
    public override OrganizationActionKind Kind => OrganizationActionKind.CopyToDirectory;

    /// <inheritdoc />
    public override IReadOnlyList<ActionValidationProblem> Validate(PathPolicy pathPolicy) =>
        ValidateDestinationDirectory(DestinationDirectory, pathPolicy);
}
