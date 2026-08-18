namespace FileOrganizer.Application.Watching;

/// <summary>Read-only multi-root watch and planning-preview lifecycle boundary.</summary>
public interface IWatchPreviewRuntime : IAsyncDisposable
{
    event EventHandler<WatchPreviewRuntimeNotification>? SnapshotChanged;

    WatchPreviewRuntimeSnapshot Snapshot { get; }

    void Start();

    bool PauseRoot(Guid watchRootId);

    bool ResumeRoot(Guid watchRootId);

    bool RequestBaselineScan(Guid watchRootId);

    ValueTask DrainAsync(CancellationToken cancellationToken = default);
}
