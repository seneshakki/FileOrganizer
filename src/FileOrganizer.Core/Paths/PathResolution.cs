namespace FileOrganizer.Core.Paths;

/// <summary>The result of parsing or combining a path without touching a file system.</summary>
public sealed class PathResolution
{
    private PathResolution(CanonicalPath? path, PathProblem? problem)
    {
        Path = path;
        Problem = problem;
    }

    /// <summary>Gets whether a canonical path was produced.</summary>
    public bool Succeeded => Path is not null;

    /// <summary>Gets the path, or <see langword="null"/> when resolution failed.</summary>
    public CanonicalPath? Path { get; }

    /// <summary>Gets the failure, or <see langword="null"/> when resolution succeeded.</summary>
    public PathProblem? Problem { get; }

    internal static PathResolution Success(CanonicalPath path) => new(path, null);

    internal static PathResolution Failure(PathProblemKind kind, string description) =>
        new(null, new PathProblem(kind, description));

    internal static PathResolution Failure(PathProblem problem) => new(null, problem);
}
