namespace FileOrganizer.Application.Watching;

/// <summary>Identifies why a read-only watched-root preview was requested.</summary>
public enum WatchRootPreviewTrigger
{
    Baseline = 0,
    StableCandidate,
    RefreshRequested,
}
