namespace FileOrganizer.Application.Watching;

/// <summary>The explicit trust and lifecycle state of one watched root.</summary>
public enum WatchRootState
{
    RequiresInitialScan = 0,
    Synchronized,
    Paused,
    RescanRequired,
    Faulted,
    Unavailable,
}
