using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;

namespace FileOrganizer.Application.Execution;

/// <summary>
/// One deterministic move operation compiled from a ready dry-run plan item.
/// </summary>
public sealed class ExecutionManifestItem
{
    internal ExecutionManifestItem(
        int ordinal,
        SourcePrecondition sourcePrecondition,
        CanonicalPath destinationDirectory,
        PhysicalEntryIdentity destinationDirectoryPhysicalIdentity,
        CanonicalPath targetPath,
        string selectedRuleName,
        int selectedRuleOrder)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentNullException.ThrowIfNull(sourcePrecondition);
        ArgumentNullException.ThrowIfNull(destinationDirectory);
        ArgumentNullException.ThrowIfNull(destinationDirectoryPhysicalIdentity);
        ArgumentNullException.ThrowIfNull(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedRuleName);

        if (sourcePrecondition.CanonicalPath.Semantics != destinationDirectory.Semantics
            || sourcePrecondition.CanonicalPath.Semantics != targetPath.Semantics)
        {
            throw new ArgumentException(
                "Every manifest path must use the same explicit path semantics.",
                nameof(targetPath));
        }

        if (sourcePrecondition.PhysicalIdentity is null)
        {
            throw new ArgumentException(
                "A manifest source must retain strong physical identity.",
                nameof(sourcePrecondition));
        }

        if (destinationDirectoryPhysicalIdentity.EntryKind != PhysicalEntryKind.Directory)
        {
            throw new ArgumentException(
                "The destination identity must identify a directory.",
                nameof(destinationDirectoryPhysicalIdentity));
        }

        Ordinal = ordinal;
        SourcePrecondition = sourcePrecondition;
        DestinationDirectory = destinationDirectory;
        DestinationDirectoryPhysicalIdentity = destinationDirectoryPhysicalIdentity;
        TargetPath = targetPath;
        SelectedRuleName = selectedRuleName;
        SelectedRuleOrder = selectedRuleOrder;
    }

    /// <summary>Gets this operation's zero-based position in deterministic execution order.</summary>
    public int Ordinal { get; }

    /// <summary>Gets the immutable source metadata that must be revalidated before execution.</summary>
    public SourcePrecondition SourcePrecondition { get; }

    /// <summary>Gets the existing directory that must be revalidated before execution.</summary>
    public CanonicalPath DestinationDirectory { get; }

    /// <summary>Gets the destination directory identity captured during planning.</summary>
    public PhysicalEntryIdentity DestinationDirectoryPhysicalIdentity { get; }

    /// <summary>Gets the exact no-overwrite target path.</summary>
    public CanonicalPath TargetPath { get; }

    /// <summary>Gets the rule name retained for audit output.</summary>
    public string SelectedRuleName { get; }

    /// <summary>Gets the configured rule order retained for audit output.</summary>
    public int SelectedRuleOrder { get; }

    /// <summary>Gets the only operation kind supported by M3B-1 manifests.</summary>
    public OrganizationActionKind ActionKind { get; } = OrganizationActionKind.MoveToDirectory;
}
