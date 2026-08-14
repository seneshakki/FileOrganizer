namespace FileOrganizer.Application.Watching;

/// <summary>The untrusted hint kind reported by a physical change source.</summary>
public enum FileChangeSourceEventKind
{
    Created = 0,
    Changed,
    Renamed,
    Deleted,
    WatcherError,
    Overflow,
}
