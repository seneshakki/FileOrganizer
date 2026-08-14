using System.ComponentModel;
using System.Security;
using FileOrganizer.Application.Watching;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.Infrastructure.Watching;

/// <summary>Creates read-only change sources backed by <see cref="FileSystemWatcher"/>.</summary>
public sealed class FileSystemWatcherChangeSourceFactory : IFileChangeSourceFactory
{
    internal const int PendingChangeCapacity = 256;

    internal const NotifyFilters ObservedChanges =
        NotifyFilters.FileName
        | NotifyFilters.DirectoryName
        | NotifyFilters.Attributes
        | NotifyFilters.Size
        | NotifyFilters.LastWrite
        | NotifyFilters.CreationTime
        | NotifyFilters.Security;

    private readonly IFileSystemWatcherSessionFactory _sessionFactory;

    public FileSystemWatcherChangeSourceFactory()
        : this(new SystemFileSystemWatcherSessionFactory())
    {
    }

    internal FileSystemWatcherChangeSourceFactory(
        IFileSystemWatcherSessionFactory sessionFactory)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        _sessionFactory = sessionFactory;
    }

    public FileChangeSourceOpenResult Open(FileChangeSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsHostPathSyntax(request.RootPath.Semantics.Syntax))
        {
            return FileChangeSourceOpenResult.Unsupported(
                "The configured path syntax is not supported by this host.");
        }

        IFileSystemWatcherSession? session = null;
        try
        {
            session = _sessionFactory.Create(request.RootPath.Value);
            session.Filter = "*";
            session.NotifyFilter = ObservedChanges;
            session.IncludeSubdirectories = request.IncludeSubdirectories;

            FileSystemWatcherChangeSource source = new(session);
            session = null;
            return FileChangeSourceOpenResult.Opened(source);
        }
        catch (PlatformNotSupportedException)
        {
            return FileChangeSourceOpenResult.Unsupported(
                "Filesystem change notifications are not supported by this host.");
        }
        catch (NotSupportedException)
        {
            return FileChangeSourceOpenResult.Unsupported(
                "Filesystem change notifications are not supported for this path.");
        }
        catch (UnauthorizedAccessException)
        {
            return Unavailable(
                FileChangeSourceProblemKind.Inaccessible,
                "The watched directory could not be accessed.");
        }
        catch (SecurityException)
        {
            return Unavailable(
                FileChangeSourceProblemKind.Inaccessible,
                "The watched directory could not be accessed.");
        }
        catch (IOException)
        {
            return Unavailable(
                FileChangeSourceProblemKind.InputOutputFailure,
                "The filesystem watcher could not be opened.");
        }
        catch (ArgumentException)
        {
            return Unavailable(
                FileChangeSourceProblemKind.Unavailable,
                "The filesystem watcher rejected the configured directory.");
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static bool IsHostPathSyntax(PathSyntax syntax) =>
        OperatingSystem.IsWindows()
            ? syntax == PathSyntax.Windows
            : syntax == PathSyntax.Posix;

    private static FileChangeSourceOpenResult Unavailable(
        FileChangeSourceProblemKind kind,
        string description) =>
        FileChangeSourceOpenResult.Unavailable(new FileChangeSourceProblem(kind, description));

    private sealed class FileSystemWatcherChangeSource : IFileChangeSource
    {
        private readonly object _gate = new();
        private readonly Queue<FileChangeSourceEvent> _pendingChanges = new(PendingChangeCapacity);
        private readonly IFileSystemWatcherSession _session;
        private bool _disposed;
        private bool _dispatching;
        private bool _saturated;
        private bool _started;
        private long _sequence;
        private EventHandler<FileChangeSourceEvent>? _eventReceived;

        internal FileSystemWatcherChangeSource(IFileSystemWatcherSession session)
        {
            _session = session;
            _session.Created += OnCreated;
            _session.Changed += OnChanged;
            _session.Renamed += OnRenamed;
            _session.Deleted += OnDeleted;
            _session.Error += OnError;
        }

        public event EventHandler<FileChangeSourceEvent>? EventReceived
        {
            add
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _eventReceived += value;
                }
            }
            remove
            {
                lock (_gate)
                {
                    _eventReceived -= value;
                }
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_started)
                {
                    return;
                }

                _started = true;
            }

            try
            {
                _session.EnableRaisingEvents = true;
            }
            catch (Exception exception) when (IsExpectedWatcherFailure(exception))
            {
                Publish(
                    sequence => CreateProblemEvent(exception, sequence),
                    forceOverflow: exception is InternalBufferOverflowException);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _started = false;
                _pendingChanges.Clear();
                _eventReceived = null;
            }

            _session.Created -= OnCreated;
            _session.Changed -= OnChanged;
            _session.Renamed -= OnRenamed;
            _session.Deleted -= OnDeleted;
            _session.Error -= OnError;
            _session.Dispose();
        }

        private void OnCreated(object sender, FileSystemEventArgs eventArgs) =>
            PublishPath(FileChangeSourceEventKind.Created, eventArgs.FullPath);

        private void OnChanged(object sender, FileSystemEventArgs eventArgs) =>
            PublishPath(FileChangeSourceEventKind.Changed, eventArgs.FullPath);

        private void OnDeleted(object sender, FileSystemEventArgs eventArgs) =>
            PublishPath(FileChangeSourceEventKind.Deleted, eventArgs.FullPath);

        private void OnRenamed(object sender, RenamedEventArgs eventArgs) =>
            Publish(
                sequence => new FileChangeSourceEvent(
                    FileChangeSourceEventKind.Renamed,
                    sequence,
                    eventArgs.FullPath,
                    eventArgs.OldFullPath));

        private void OnError(object sender, ErrorEventArgs eventArgs)
        {
            Exception exception = eventArgs.GetException();
            Publish(
                sequence => CreateProblemEvent(exception, sequence),
                forceOverflow: exception is InternalBufferOverflowException);
        }

        private void PublishPath(FileChangeSourceEventKind kind, string path) =>
            Publish(sequence => new FileChangeSourceEvent(kind, sequence, path));

        private void Publish(
            Func<long, FileChangeSourceEvent> create,
            bool forceOverflow = false)
        {
            bool shouldDispatch;
            lock (_gate)
            {
                if (_disposed || !_started || _saturated)
                {
                    return;
                }

                if (forceOverflow || _pendingChanges.Count >= PendingChangeCapacity)
                {
                    _saturated = true;
                    _pendingChanges.Clear();
                    _pendingChanges.Enqueue(CreateOverflowEvent(checked(++_sequence)));
                }
                else
                {
                    _pendingChanges.Enqueue(create(checked(++_sequence)));
                }

                shouldDispatch = !_dispatching;
                _dispatching = true;
            }

            if (shouldDispatch)
            {
                DrainPendingChanges();
            }
        }

        private void DrainPendingChanges()
        {
            while (true)
            {
                FileChangeSourceEvent change;
                EventHandler<FileChangeSourceEvent>? handlers;
                lock (_gate)
                {
                    if (_disposed || _pendingChanges.Count == 0)
                    {
                        _pendingChanges.Clear();
                        _dispatching = false;
                        return;
                    }

                    change = _pendingChanges.Dequeue();
                    handlers = _eventReceived;
                }

                DispatchSafely(handlers, change);
            }
        }

        private void DispatchSafely(
            EventHandler<FileChangeSourceEvent>? handlers,
            FileChangeSourceEvent change)
        {
            if (handlers is null)
            {
                return;
            }

            foreach (Delegate candidate in handlers.GetInvocationList())
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                }

                try
                {
                    ((EventHandler<FileChangeSourceEvent>)candidate)(this, change);
                }
                catch (Exception exception) when (IsContainableSubscriberFailure(exception))
                {
                    // A consumer cannot unwind through a native watcher callback or prevent
                    // another consumer from receiving the same safety-relevant hint.
                }
            }
        }

        private static FileChangeSourceEvent CreateProblemEvent(
            Exception exception,
            long sequence)
        {
            if (exception is InternalBufferOverflowException)
            {
                return CreateOverflowEvent(sequence);
            }

            (FileChangeSourceProblemKind kind, string description) = exception switch
            {
                UnauthorizedAccessException or SecurityException =>
                    (FileChangeSourceProblemKind.Inaccessible,
                        "The filesystem watcher lost access to its directory."),
                PlatformNotSupportedException or NotSupportedException =>
                    (FileChangeSourceProblemKind.UnsupportedCapability,
                        "Filesystem change notifications became unavailable."),
                IOException =>
                    (FileChangeSourceProblemKind.InputOutputFailure,
                        "The filesystem watcher reported an input/output failure."),
                _ =>
                    (FileChangeSourceProblemKind.Unavailable,
                        "The filesystem watcher reported an unexpected failure."),
            };

            int? nativeErrorCode = exception is Win32Exception win32Exception
                && win32Exception.NativeErrorCode >= 0
                    ? win32Exception.NativeErrorCode
                    : null;
            return new FileChangeSourceEvent(
                FileChangeSourceEventKind.WatcherError,
                sequence,
                problem: new FileChangeSourceProblem(kind, description, nativeErrorCode));
        }

        private static FileChangeSourceEvent CreateOverflowEvent(long sequence) =>
            new(
                FileChangeSourceEventKind.Overflow,
                sequence,
                problem: new FileChangeSourceProblem(
                    FileChangeSourceProblemKind.BufferOverflow,
                    "The filesystem change buffer overflowed; change hints may have been lost."));

        private static bool IsExpectedWatcherFailure(Exception exception) => exception is
            UnauthorizedAccessException
            or SecurityException
            or IOException
            or PlatformNotSupportedException
            or NotSupportedException
            or InvalidOperationException;

        private static bool IsContainableSubscriberFailure(Exception exception) => exception is not
            OutOfMemoryException
            and not AccessViolationException;
    }
}
