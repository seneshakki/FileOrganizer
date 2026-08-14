namespace FileOrganizer.Infrastructure.Watching;

internal sealed class SystemFileSystemWatcherSessionFactory : IFileSystemWatcherSessionFactory
{
    public IFileSystemWatcherSession Create(string path) =>
        new SystemFileSystemWatcherSession(new FileSystemWatcher(path));
}

internal sealed class SystemFileSystemWatcherSession : IFileSystemWatcherSession
{
    private readonly FileSystemWatcher _watcher;

    internal SystemFileSystemWatcherSession(FileSystemWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        _watcher = watcher;
    }

    public event FileSystemEventHandler? Changed
    {
        add => _watcher.Changed += value;
        remove => _watcher.Changed -= value;
    }

    public event FileSystemEventHandler? Created
    {
        add => _watcher.Created += value;
        remove => _watcher.Created -= value;
    }

    public event FileSystemEventHandler? Deleted
    {
        add => _watcher.Deleted += value;
        remove => _watcher.Deleted -= value;
    }

    public event RenamedEventHandler? Renamed
    {
        add => _watcher.Renamed += value;
        remove => _watcher.Renamed -= value;
    }

    public event ErrorEventHandler? Error
    {
        add => _watcher.Error += value;
        remove => _watcher.Error -= value;
    }

    public string Filter
    {
        get => _watcher.Filter;
        set => _watcher.Filter = value;
    }

    public NotifyFilters NotifyFilter
    {
        get => _watcher.NotifyFilter;
        set => _watcher.NotifyFilter = value;
    }

    public bool IncludeSubdirectories
    {
        get => _watcher.IncludeSubdirectories;
        set => _watcher.IncludeSubdirectories = value;
    }

    public bool EnableRaisingEvents
    {
        get => _watcher.EnableRaisingEvents;
        set => _watcher.EnableRaisingEvents = value;
    }

    public void Dispose() => _watcher.Dispose();
}
