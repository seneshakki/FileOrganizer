namespace FileOrganizer.Application.Watching;

/// <summary>One immutable reason that a read-only watched-root preview was not published.</summary>
public sealed record WatchRootPreviewProblem
{
    public WatchRootPreviewProblem(WatchRootPreviewProblemKind kind, string description)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Kind = kind;
        Description = description;
    }

    public WatchRootPreviewProblemKind Kind { get; }

    public string Description { get; }
}
