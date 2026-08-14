namespace FileOrganizer.Application.Watching;

/// <summary>A stable category for a physical watcher failure.</summary>
public enum FileChangeSourceProblemKind
{
    BufferOverflow = 0,
    Inaccessible,
    InputOutputFailure,
    UnsupportedCapability,
    Unavailable,
}
