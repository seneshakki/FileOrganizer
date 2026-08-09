using System.Collections.ObjectModel;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Rules;

namespace FileOrganizer.Core.Planning;

/// <summary>The immutable dry-run decision for one scanned source file.</summary>
public sealed class OrganizationPlanItem
{
    private readonly ReadOnlyCollection<PlanReason> _reasons;

    internal OrganizationPlanItem(
        FileEntry source,
        SourcePrecondition sourcePrecondition,
        RuleSetEvaluation? ruleEvaluation,
        OrganizationRule? selectedRule,
        OrganizationAction? proposedAction,
        CanonicalPath? proposedTargetPath,
        CanonicalPath? destinationDirectory,
        PhysicalEntryIdentity? destinationDirectoryPhysicalIdentity,
        PlanItemOutcome outcome,
        IEnumerable<PlanReason> reasons)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourcePrecondition);
        ArgumentNullException.ThrowIfNull(reasons);

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        PlanReason[] reasonSnapshot = [.. reasons];
        foreach (PlanReason reason in reasonSnapshot)
        {
            ArgumentNullException.ThrowIfNull(reason, nameof(reasons));
        }

        if (outcome == PlanItemOutcome.Ready
            && (selectedRule is null
                || proposedAction is null
                || proposedTargetPath is null
                || destinationDirectory is null))
        {
            throw new ArgumentException(
                "A ready item must have a selected rule, action, target, and destination directory.",
                nameof(outcome));
        }

        if (destinationDirectoryPhysicalIdentity?.EntryKind
            is not (null or PhysicalEntryKind.Directory))
        {
            throw new ArgumentException(
                "A destination directory can retain only a directory physical identity.",
                nameof(destinationDirectoryPhysicalIdentity));
        }

        if (destinationDirectoryPhysicalIdentity is not null
            && destinationDirectory is null)
        {
            throw new ArgumentException(
                "Destination identity requires a destination directory path.",
                nameof(destinationDirectoryPhysicalIdentity));
        }

        Source = source;
        SourcePrecondition = sourcePrecondition;
        RuleEvaluation = ruleEvaluation;
        SelectedRule = selectedRule;
        ProposedAction = proposedAction;
        ProposedTargetPath = proposedTargetPath;
        DestinationDirectory = destinationDirectory;
        DestinationDirectoryPhysicalIdentity = destinationDirectoryPhysicalIdentity;
        Outcome = outcome;
        _reasons = Array.AsReadOnly(reasonSnapshot);
    }

    /// <summary>Gets the source snapshot evaluated by the planner.</summary>
    public FileEntry Source { get; }

    /// <summary>Gets the source metadata a future executor must revalidate.</summary>
    public SourcePrecondition SourcePrecondition { get; }

    /// <summary>
    /// Gets the complete matching audit, or <see langword="null"/> when an
    /// unsafe source path prevented rule evaluation.
    /// </summary>
    public RuleSetEvaluation? RuleEvaluation { get; }

    /// <summary>Gets the association whose matching rule applied, if any.</summary>
    public OrganizationRule? SelectedRule { get; }

    /// <summary>Gets the selected planning intent, if a rule applied.</summary>
    public OrganizationAction? ProposedAction { get; }

    /// <summary>Gets the canonical proposed target, if one could be resolved.</summary>
    public CanonicalPath? ProposedTargetPath { get; }

    /// <summary>Gets the directory that must contain the proposed target.</summary>
    public CanonicalPath? DestinationDirectory { get; }

    /// <summary>
    /// Gets the strong identity captured for the required destination directory,
    /// or <see langword="null"/> for a preview-only proposal.
    /// </summary>
    public PhysicalEntryIdentity? DestinationDirectoryPhysicalIdentity { get; }

    /// <summary>Gets the exact item-level outcome.</summary>
    public PlanItemOutcome Outcome { get; }

    /// <summary>Gets structured reasons in deterministic order.</summary>
    public IReadOnlyList<PlanReason> Reasons => _reasons;
}
