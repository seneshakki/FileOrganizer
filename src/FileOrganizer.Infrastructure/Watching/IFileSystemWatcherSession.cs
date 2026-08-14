namespace FileOrganizer.Infrastructure.Watching;

/// <summary>The narrow, testable surface used from <see cref="FileSystemWatcher"/>.</summary>
internal interface IFileSystemWatcherSession : IDisposable
{
    event FileSystemEventHandler? Changed;

    event FileSystemEventHandler? Created;

    event FileSystemEventHandler? Deleted;

    event RenamedEventHandler? Renamed;

    event ErrorEventHandler? Error;

    string Filter { get; set; }

    NotifyFilters NotifyFilter { get; set; }

    bool IncludeSubdirectories { get; set; }

    bool EnableRaisingEvents { get; set; }
}

internal interface IFileSystemWatcherSessionFactory
{
    IFileSystemWatcherSession Create(string path);
}
