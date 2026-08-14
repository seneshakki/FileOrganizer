namespace FileOrganizer.Application.Watching;

/// <summary>The structured result of creating one dormant physical watcher.</summary>
public sealed class FileChangeSourceOpenResult
{
    private FileChangeSourceOpenResult(
        FileChangeSourceOpenStatus status,
        IFileChangeSource? source,
        FileChangeSourceProblem? problem)
    {
        bool valid = status switch
        {
            FileChangeSourceOpenStatus.Opened => source is not null && problem is null,
            FileChangeSourceOpenStatus.Unavailable =>
                source is null && problem is not null,
            FileChangeSourceOpenStatus.Unsupported =>
                source is null
                && problem?.Kind == FileChangeSourceProblemKind.UnsupportedCapability,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("The source-open result is contradictory.", nameof(status));
        }

        Status = status;
        Source = source;
        Problem = problem;
    }

    public FileChangeSourceOpenStatus Status { get; }

    public IFileChangeSource? Source { get; }

    public FileChangeSourceProblem? Problem { get; }

    public static FileChangeSourceOpenResult Opened(IFileChangeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(FileChangeSourceOpenStatus.Opened, source, null);
    }

    public static FileChangeSourceOpenResult Unavailable(FileChangeSourceProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new(FileChangeSourceOpenStatus.Unavailable, null, problem);
    }

    public static FileChangeSourceOpenResult Unsupported(string description) =>
        new(
            FileChangeSourceOpenStatus.Unsupported,
            null,
            new FileChangeSourceProblem(
                FileChangeSourceProblemKind.UnsupportedCapability,
                description));
}
