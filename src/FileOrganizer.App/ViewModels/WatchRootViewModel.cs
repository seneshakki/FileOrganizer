using System.Collections.ObjectModel;
using FileOrganizer.Application.Watching;
using FileOrganizer.Core.Planning;

namespace FileOrganizer.App.ViewModels;

public sealed class WatchRootViewModel
{
    private readonly ReadOnlyCollection<PlanItemViewModel> _planItems;

    public WatchRootViewModel(
        WatchPreviewRootSnapshot snapshot,
        Func<Guid, bool> pause,
        Func<Guid, bool> resume,
        Func<Guid, bool> reconnect,
        Func<Guid, bool> retry)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(resume);
        ArgumentNullException.ThrowIfNull(reconnect);
        ArgumentNullException.ThrowIfNull(retry);

        Id = snapshot.WatchRootId;
        RootPath = snapshot.RootPath.Value;
        IsEnabled = snapshot.IsEnabled;
        IncludeSubdirectories = snapshot.IncludeSubdirectories;
        State = snapshot.State.ToString();
        StateDescription = DescribeState(snapshot.State);
        LatestDiagnostic = snapshot.LatestDiagnostic is null
            ? "No watcher diagnostic"
            : $"{snapshot.LatestDiagnostic.Kind}: {snapshot.LatestDiagnostic.Description}";

        WatchRootPreviewResult? preview = snapshot.LatestPreview;
        PreviewOutcome = preview?.Outcome.ToString() ?? "Not produced";
        PreviewTrigger = preview?.Trigger.ToString() ?? "—";
        PreviewProblem = preview is null || preview.Problems.Count == 0
            ? "No preview problem"
            : string.Join(" · ", preview.Problems.Select(static problem =>
                $"{problem.Kind}: {problem.Description}"));

        OrganizationPlan? plan = preview?.Plan;
        PlanGeneratedAt = plan is null
            ? "—"
            : plan.GeneratedAtUtc.ToString("u", System.Globalization.CultureInfo.InvariantCulture);
        PlanIsComplete = plan?.IsScanComplete == true ? "Complete scan" : "No complete scan";
        TotalCount = plan?.Items.Count ?? 0;
        ReadyCount = Count(plan, PlanItemOutcome.Ready);
        SkippedCount = Count(plan, PlanItemOutcome.Skipped);
        BlockedCount = Count(plan, PlanItemOutcome.Blocked);
        ConflictCount = Count(plan, PlanItemOutcome.Conflict);
        _planItems = Array.AsReadOnly(
            plan?.Items.Select(static item => new PlanItemViewModel(item)).ToArray() ?? []);
        HasPlanItems = _planItems.Count != 0;

        PauseCommand = new DelegateCommand(
            () => pause(Id),
            () => IsEnabled && snapshot.State != WatchRootState.Paused);
        ResumeCommand = new DelegateCommand(
            () => resume(Id),
            () => IsEnabled && snapshot.State == WatchRootState.Paused);
        ReconnectCommand = new DelegateCommand(
            () => reconnect(Id),
            () => IsEnabled
                && snapshot.State is WatchRootState.Faulted
                    or WatchRootState.Unavailable);
        RetryCommand = new DelegateCommand(
            () => retry(Id),
            () => IsEnabled
                && snapshot.State is WatchRootState.RequiresInitialScan
                    or WatchRootState.RescanRequired);
    }

    public Guid Id { get; }

    public string RootPath { get; }

    public bool IsEnabled { get; }

    public bool IncludeSubdirectories { get; }

    public string State { get; }

    public string StateDescription { get; }

    public string LatestDiagnostic { get; }

    public string PreviewOutcome { get; }

    public string PreviewTrigger { get; }

    public string PreviewProblem { get; }

    public string PlanGeneratedAt { get; }

    public string PlanIsComplete { get; }

    public int TotalCount { get; }

    public int ReadyCount { get; }

    public int SkippedCount { get; }

    public int BlockedCount { get; }

    public int ConflictCount { get; }

    public bool HasPlanItems { get; }

    public IReadOnlyList<PlanItemViewModel> PlanItems => _planItems;

    public DelegateCommand PauseCommand { get; }

    public DelegateCommand ResumeCommand { get; }

    public DelegateCommand ReconnectCommand { get; }

    public DelegateCommand RetryCommand { get; }

    private static int Count(OrganizationPlan? plan, PlanItemOutcome outcome) =>
        plan?.Items.Count(item => item.Outcome == outcome) ?? 0;

    private static string DescribeState(WatchRootState state) => state switch
    {
        WatchRootState.RequiresInitialScan => "Waiting for a trusted full-root baseline",
        WatchRootState.Synchronized => "Incremental events are trusted for this generation",
        WatchRootState.Paused => "Physical watching is stopped; resume requires a new baseline",
        WatchRootState.RescanRequired => "Events may be missing; a full-root scan is required",
        WatchRootState.Faulted => "The watcher failed closed",
        WatchRootState.Unavailable => "The configured root is currently unavailable",
        _ => "Unknown watcher state",
    };
}
