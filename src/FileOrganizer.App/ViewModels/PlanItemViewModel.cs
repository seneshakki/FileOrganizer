using FileOrganizer.Core.Planning;

namespace FileOrganizer.App.ViewModels;

public sealed class PlanItemViewModel
{
    public PlanItemViewModel(OrganizationPlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        SourceName = item.Source.Name;
        SourcePath = item.Source.FullPath;
        Outcome = item.Outcome.ToString();
        Action = item.ProposedAction?.Kind.ToString() ?? "No action";
        TargetPath = item.ProposedTargetPath?.Value ?? "—";
        Reasons = item.Reasons.Count == 0
            ? "No blocking reasons"
            : string.Join(" · ", item.Reasons.Select(static reason => reason.Description));
    }

    public string SourceName { get; }

    public string SourcePath { get; }

    public string Outcome { get; }

    public string Action { get; }

    public string TargetPath { get; }

    public string Reasons { get; }
}
