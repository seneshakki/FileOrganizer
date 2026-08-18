namespace FileOrganizer.Application.Watching;

/// <summary>The fail-closed result of one read-only watched-root preview request.</summary>
public enum WatchRootPreviewOutcome
{
    Ready = 0,
    Suppressed,
    RescanRequired,
    Failed,
}
