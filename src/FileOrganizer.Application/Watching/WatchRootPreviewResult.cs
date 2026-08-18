using System.Collections.ObjectModel;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;

namespace FileOrganizer.Application.Watching;

/// <summary>An immutable, historical planning-only result for one watched-root refresh.</summary>
/// <remarks>
/// A ready result records a snapshot that was current immediately before publication. It grants
/// no execution capability; every future privileged boundary must independently revalidate it.
/// </remarks>
public sealed class WatchRootPreviewResult
{
    private readonly ReadOnlyCollection<WatchRootPreviewProblem> _problems;

    private WatchRootPreviewResult(
        Guid watchRootId,
        WatchRootPreviewTrigger trigger,
        WatchRootPreviewOutcome outcome,
        OrganizationPlan? plan,
        WatchSnapshotToken? snapshotToken,
        StableFileCandidate? candidate,
        CanonicalPath? affectedPath,
        IEnumerable<WatchRootPreviewProblem> problems)
    {
        if (watchRootId == Guid.Empty)
        {
            throw new ArgumentException("A preview result requires a watch-root ID.", nameof(watchRootId));
        }

        if (!Enum.IsDefined(trigger))
        {
            throw new ArgumentOutOfRangeException(nameof(trigger));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        ArgumentNullException.ThrowIfNull(problems);
        WatchRootPreviewProblem[] problemSnapshot = [.. problems];
        if (problemSnapshot.Any(static problem => problem is null))
        {
            throw new ArgumentException("Preview problems cannot contain null.", nameof(problems));
        }

        bool isReady = outcome == WatchRootPreviewOutcome.Ready;
        if (isReady != (plan is not null
            && snapshotToken is not null
            && problemSnapshot.Length == 0))
        {
            throw new ArgumentException(
                "Only a problem-free ready result can contain a plan.",
                nameof(outcome));
        }

        if (!isReady && problemSnapshot.Length == 0)
        {
            throw new ArgumentException(
                "A non-ready preview result requires a structured problem.",
                nameof(problems));
        }

        if (snapshotToken is not null && snapshotToken.WatchRootId != watchRootId)
        {
            throw new ArgumentException(
                "A preview snapshot token must belong to the result watch root.",
                nameof(snapshotToken));
        }

        if (candidate is not null && candidate.WatchRootId != watchRootId)
        {
            throw new ArgumentException(
                "A preview candidate must belong to the result watch root.",
                nameof(candidate));
        }

        if (trigger == WatchRootPreviewTrigger.StableCandidate != (candidate is not null))
        {
            throw new ArgumentException(
                "Only a stable-candidate preview can retain a candidate.",
                nameof(candidate));
        }

        if (trigger == WatchRootPreviewTrigger.RefreshRequested != (affectedPath is not null))
        {
            throw new ArgumentException(
                "Only a refresh-request preview can retain an affected path.",
                nameof(affectedPath));
        }

        WatchRootId = watchRootId;
        Trigger = trigger;
        Outcome = outcome;
        Plan = plan;
        SnapshotToken = snapshotToken;
        Candidate = candidate;
        AffectedPath = affectedPath;
        _problems = Array.AsReadOnly(problemSnapshot);
    }

    public Guid WatchRootId { get; }

    public WatchRootPreviewTrigger Trigger { get; }

    public WatchRootPreviewOutcome Outcome { get; }

    /// <summary>Gets the historical dry-run plan; this is never authorization to execute.</summary>
    public OrganizationPlan? Plan { get; }

    /// <summary>Gets the synchronized generation proven current before publication.</summary>
    public WatchSnapshotToken? SnapshotToken { get; }

    public StableFileCandidate? Candidate { get; }

    public CanonicalPath? AffectedPath { get; }

    public IReadOnlyList<WatchRootPreviewProblem> Problems => _problems;

    /// <summary>
    /// Creates a historical ready preview for presentation or runtime-boundary implementations.
    /// The result is never authorization to execute its plan.
    /// </summary>
    public static WatchRootPreviewResult CreateReady(
        Guid watchRootId,
        WatchRootPreviewTrigger trigger,
        OrganizationPlan plan,
        WatchSnapshotToken snapshotToken,
        StableFileCandidate? candidate = null,
        CanonicalPath? affectedPath = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshotToken);
        return new(
            watchRootId,
            trigger,
            WatchRootPreviewOutcome.Ready,
            plan,
            snapshotToken,
            candidate,
            affectedPath,
            []);
    }

    /// <summary>
    /// Creates a structured historical non-ready preview for presentation or runtime fakes.
    /// The result grants no execution capability.
    /// </summary>
    public static WatchRootPreviewResult CreateNotReady(
        Guid watchRootId,
        WatchRootPreviewTrigger trigger,
        WatchRootPreviewOutcome outcome,
        WatchRootPreviewProblem problem,
        WatchSnapshotToken? snapshotToken = null,
        StableFileCandidate? candidate = null,
        CanonicalPath? affectedPath = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new(
            watchRootId,
            trigger,
            outcome,
            null,
            snapshotToken,
            candidate,
            affectedPath,
            [problem]);
    }
}
