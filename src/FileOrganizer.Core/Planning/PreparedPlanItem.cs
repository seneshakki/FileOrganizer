using System.Collections.ObjectModel;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Rules;

namespace FileOrganizer.Core.Planning;

internal sealed class PreparedPlanItem
{
    private readonly ReadOnlyCollection<PlanReason> _reasons;

    public PreparedPlanItem(
        FileEntry source,
        SourcePrecondition sourcePrecondition,
        RuleSetEvaluation? ruleEvaluation,
        OrganizationRule? selectedRule,
        OrganizationAction? proposedAction,
        CanonicalPath? proposedTargetPath,
        CanonicalPath? destinationDirectory,
        PlanItemOutcome outcome,
        IEnumerable<PlanReason> reasons)
    {
        Source = source;
        SourcePrecondition = sourcePrecondition;
        RuleEvaluation = ruleEvaluation;
        SelectedRule = selectedRule;
        ProposedAction = proposedAction;
        ProposedTargetPath = proposedTargetPath;
        DestinationDirectory = destinationDirectory;
        Outcome = outcome;
        _reasons = Array.AsReadOnly(reasons.ToArray());
    }

    public FileEntry Source { get; }

    public SourcePrecondition SourcePrecondition { get; }

    public RuleSetEvaluation? RuleEvaluation { get; }

    public OrganizationRule? SelectedRule { get; }

    public OrganizationAction? ProposedAction { get; }

    public CanonicalPath? ProposedTargetPath { get; }

    public CanonicalPath? DestinationDirectory { get; }

    public PlanItemOutcome Outcome { get; }

    public IReadOnlyList<PlanReason> Reasons => _reasons;
}
