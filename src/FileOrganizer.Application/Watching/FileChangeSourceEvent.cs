namespace FileOrganizer.Application.Watching;

/// <summary>An immutable raw watcher hint. Paths remain untrusted until Application validates them.</summary>
public sealed record FileChangeSourceEvent
{
    public FileChangeSourceEvent(
        FileChangeSourceEventKind kind,
        long sequence,
        string? path = null,
        string? oldPath = null,
        FileChangeSourceProblem? problem = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        bool valid = kind switch
        {
            FileChangeSourceEventKind.Created
                or FileChangeSourceEventKind.Changed
                or FileChangeSourceEventKind.Deleted =>
                    !string.IsNullOrWhiteSpace(path) && oldPath is null && problem is null,
            FileChangeSourceEventKind.Renamed =>
                !string.IsNullOrWhiteSpace(path)
                && !string.IsNullOrWhiteSpace(oldPath)
                && problem is null,
            FileChangeSourceEventKind.WatcherError =>
                path is null
                && oldPath is null
                && problem is not null
                && problem.Kind != FileChangeSourceProblemKind.BufferOverflow,
            FileChangeSourceEventKind.Overflow =>
                path is null
                && oldPath is null
                && problem?.Kind == FileChangeSourceProblemKind.BufferOverflow,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("The change kind, paths, and problem describe contradictory state.");
        }

        Kind = kind;
        Sequence = sequence;
        Path = path;
        OldPath = oldPath;
        Problem = problem;
    }

    public FileChangeSourceEventKind Kind { get; }

    /// <summary>Gets a monotonically increasing sequence scoped to one source instance.</summary>
    public long Sequence { get; }

    public string? Path { get; }

    public string? OldPath { get; }

    public FileChangeSourceProblem? Problem { get; }
}
