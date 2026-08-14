using FileOrganizer.Core.Paths;

namespace FileOrganizer.Application.Watching;

/// <summary>The immutable scope requested from one physical watcher instance.</summary>
public sealed record FileChangeSourceRequest
{
    public FileChangeSourceRequest(
        Guid watchRootId,
        CanonicalPath rootPath,
        bool includeSubdirectories)
    {
        if (watchRootId == Guid.Empty)
        {
            throw new ArgumentException("A watch-root ID cannot be empty.", nameof(watchRootId));
        }

        ArgumentNullException.ThrowIfNull(rootPath);
        WatchRootId = watchRootId;
        RootPath = rootPath;
        IncludeSubdirectories = includeSubdirectories;
    }

    public Guid WatchRootId { get; }

    public CanonicalPath RootPath { get; }

    public bool IncludeSubdirectories { get; }
}
