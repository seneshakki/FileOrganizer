namespace FileOrganizer.Core.Paths;

/// <summary>A structured explanation of a failed lexical path operation.</summary>
public sealed record PathProblem
{
    public PathProblem(PathProblemKind kind, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        Kind = kind;
        Description = description;
    }

    /// <summary>Gets the category callers can handle without parsing text.</summary>
    public PathProblemKind Kind { get; }

    /// <summary>Gets a concise explanation suitable for a dry-run preview.</summary>
    public string Description { get; }
}
