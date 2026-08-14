namespace FileOrganizer.Application.Watching;

/// <summary>A dormant physical watcher that reports untrusted hints after <see cref="Start"/>.</summary>
public interface IFileChangeSource : IDisposable
{
    event EventHandler<FileChangeSourceEvent>? EventReceived;

    /// <summary>Begins observation after Application has subscribed to events.</summary>
    void Start();
}
