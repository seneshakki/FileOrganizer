using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Execution;

/// <summary>
/// Transport-neutral fields required to reconstruct one persisted move manifest item.
/// </summary>
public sealed class ExecutionManifestPersistenceItem
{
    public ExecutionManifestPersistenceItem(
        int ordinal,
        OrganizationActionKind actionKind,
        string sourcePath,
        PhysicalEntryIdentity sourcePhysicalIdentity,
        long sourceLength,
        DateTimeOffset? sourceCreatedUtc,
        DateTimeOffset sourceModifiedUtc,
        string destinationDirectory,
        PhysicalEntryIdentity destinationDirectoryPhysicalIdentity,
        string targetPath,
        string selectedRuleName,
        int selectedRuleOrder)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        if (!Enum.IsDefined(actionKind))
        {
            throw new ArgumentOutOfRangeException(nameof(actionKind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(sourcePhysicalIdentity);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(destinationDirectoryPhysicalIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedRuleName);

        Ordinal = ordinal;
        ActionKind = actionKind;
        SourcePath = sourcePath;
        SourcePhysicalIdentity = sourcePhysicalIdentity;
        SourceLength = sourceLength;
        SourceCreatedUtc = sourceCreatedUtc?.ToUniversalTime();
        SourceModifiedUtc = sourceModifiedUtc.ToUniversalTime();
        DestinationDirectory = destinationDirectory;
        DestinationDirectoryPhysicalIdentity = destinationDirectoryPhysicalIdentity;
        TargetPath = targetPath;
        SelectedRuleName = selectedRuleName;
        SelectedRuleOrder = selectedRuleOrder;
    }

    public int Ordinal { get; }

    public OrganizationActionKind ActionKind { get; }

    public string SourcePath { get; }

    public PhysicalEntryIdentity SourcePhysicalIdentity { get; }

    public long SourceLength { get; }

    public DateTimeOffset? SourceCreatedUtc { get; }

    public DateTimeOffset SourceModifiedUtc { get; }

    public string DestinationDirectory { get; }

    public PhysicalEntryIdentity DestinationDirectoryPhysicalIdentity { get; }

    public string TargetPath { get; }

    public string SelectedRuleName { get; }

    public int SelectedRuleOrder { get; }
}
