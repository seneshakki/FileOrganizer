using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Paths;
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
        CanonicalPath targetPath,
        string selectedRuleName,
        int selectedRuleOrder)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentNullException.ThrowIfNull(sourcePrecondition);
        ArgumentNullException.ThrowIfNull(destinationDirectory);
        ArgumentNullException.ThrowIfNull(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedRuleName);

        if (sourcePrecondition.CanonicalPath.Semantics != destinationDirectory.Semantics
            || sourcePrecondition.CanonicalPath.Semantics != targetPath.Semantics)
        {
            throw new ArgumentException(
                "Every manifest path must use the same explicit path semantics.",
                nameof(targetPath));
        }

        Ordinal = ordinal;
        SourcePrecondition = sourcePrecondition;
        DestinationDirectory = destinationDirectory;
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

    /// <summary>Gets the exact no-overwrite target path.</summary>
    public CanonicalPath TargetPath { get; }

    /// <summary>Gets the rule name retained for audit output.</summary>
    public string SelectedRuleName { get; }

    /// <summary>Gets the configured rule order retained for audit output.</summary>
    public int SelectedRuleOrder { get; }

    /// <summary>Gets the only operation kind supported by M3B-1 manifests.</summary>
    public OrganizationActionKind ActionKind { get; } = OrganizationActionKind.MoveToDirectory;
}
