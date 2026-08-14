using FileOrganizer.Core.Paths;

namespace FileOrganizer.Application.Configuration;

/// <summary>An immutable watched root with stable configuration identity.</summary>
public sealed class WatchRootConfiguration
{
    public WatchRootConfiguration(
        Guid id,
        CanonicalPath rootPath,
        bool isEnabled,
        bool includeSubdirectories)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A watch-root ID cannot be empty.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(rootPath);

        Id = id;
        RootPath = rootPath;
        IsEnabled = isEnabled;
        IncludeSubdirectories = includeSubdirectories;
    }

    public Guid Id { get; }

    public CanonicalPath RootPath { get; }

    public bool IsEnabled { get; }

    public bool IncludeSubdirectories { get; }
}
