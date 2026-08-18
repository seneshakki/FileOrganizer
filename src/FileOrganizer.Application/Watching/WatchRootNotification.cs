using FileOrganizer.Core.Paths;

namespace FileOrganizer.Application.Watching;

/// <summary>One immutable, root-correlated output from a watch pipeline.</summary>
public sealed class WatchRootNotification
{
    private WatchRootNotification(
        Guid watchRootId,
        WatchRootNotificationKind kind,
        WatchRootState? state,
        StableFileCandidate? candidate,
        WatchDiagnostic? diagnostic,
        CanonicalPath? affectedPath)
    {
        if (watchRootId == Guid.Empty)
        {
            throw new ArgumentException("A notification requires a watch-root ID.", nameof(watchRootId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        bool valid = kind switch
        {
            WatchRootNotificationKind.StateChanged =>
                state.HasValue && candidate is null && diagnostic is null && affectedPath is null,
            WatchRootNotificationKind.Candidate =>
                state is null
                && candidate?.WatchRootId == watchRootId
                && diagnostic is null
                && affectedPath is null,
            WatchRootNotificationKind.Diagnostic =>
                state is null && candidate is null && diagnostic is not null,
            WatchRootNotificationKind.RefreshRequested =>
                state is null
                && candidate is null
                && diagnostic is null
                && affectedPath is not null,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("The notification payload is contradictory.", nameof(kind));
        }

        WatchRootId = watchRootId;
        Kind = kind;
        State = state;
        Candidate = candidate;
        Diagnostic = diagnostic;
        AffectedPath = affectedPath;
    }

    public Guid WatchRootId { get; }

    public WatchRootNotificationKind Kind { get; }

    public WatchRootState? State { get; }

    public StableFileCandidate? Candidate { get; }

    public WatchDiagnostic? Diagnostic { get; }

    /// <summary>
    /// Gets the validated path for a path-specific diagnostic or refresh request.
    /// </summary>
    public CanonicalPath? AffectedPath { get; }

    public static WatchRootNotification StateChanged(Guid watchRootId, WatchRootState state) =>
        new(watchRootId, WatchRootNotificationKind.StateChanged, state, null, null, null);

    public static WatchRootNotification CandidateReady(StableFileCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new(
            candidate.WatchRootId,
            WatchRootNotificationKind.Candidate,
            null,
            candidate,
            null,
            null);
    }

    public static WatchRootNotification DiagnosticRaised(
        Guid watchRootId,
        WatchDiagnostic diagnostic,
        CanonicalPath? affectedPath = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new(
            watchRootId,
            WatchRootNotificationKind.Diagnostic,
            null,
            null,
            diagnostic,
            affectedPath);
    }

    /// <summary>Requests that a retained preview discard or refresh one removed file.</summary>
    public static WatchRootNotification RefreshRequested(
        Guid watchRootId,
        CanonicalPath affectedPath)
    {
        ArgumentNullException.ThrowIfNull(affectedPath);
        if (affectedPath.IsRoot)
        {
            throw new ArgumentException(
                "A refresh request must identify an entry below the watch root.",
                nameof(affectedPath));
        }

        return new(
            watchRootId,
            WatchRootNotificationKind.RefreshRequested,
            null,
            null,
            null,
            affectedPath);
    }
}
