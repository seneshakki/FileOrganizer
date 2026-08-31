using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Core.Abstractions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Application.Watching;

/// <summary>
/// Owns independent read-only watch and full-root planning-preview pipelines for one configuration.
/// </summary>
/// <remarks>
/// Root previews are deliberately retained separately. This runtime has no execution, journal,
/// lease, mover, aggregate-eligibility, or user-file mutation capability.
/// </remarks>
public sealed class WatchPreviewRuntime : IWatchPreviewRuntime
{
    private readonly object _gate = new();
    private readonly OrganizationConfiguration _configuration;
    private readonly RootRuntime[] _roots;
    private readonly Dictionary<Guid, RootRuntime> _rootsById;
    private readonly HashSet<Guid> _changedRootIds = [];
    private readonly Channel<bool> _notificationSignals;
    private readonly CancellationTokenSource _notificationLifetime = new();
    private readonly Task _notificationProcessor;
    private readonly ConcurrencyLimitedFileScanner _scanner;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private TaskCompletionSource _notificationIdle = CompletedSignal();
    private Task? _disposeTask;
    private long _revision;
    private bool _notificationDispatching;
    private bool _started;
    private bool _disposeRequested;
    private bool _disposed;

    public WatchPreviewRuntime(
        OrganizationConfiguration configuration,
        IFileChangeSourceFactory sourceFactory,
        IFileStabilityObservationPort observationPort,
        IFileScanner scanner,
        IDestinationStateInspector destinationInspector,
        OrganizationPlanner planner,
        IWatchDelay delay,
        TimeProvider timeProvider,
        WatchPreviewRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        ArgumentNullException.ThrowIfNull(observationPort);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(destinationInspector);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);

        _configuration = configuration;
        _scanner = new ConcurrencyLimitedFileScanner(scanner, options.MaxConcurrentScans);
        _roots = configuration.WatchRoots
            .Select(
                watchRoot => CreateRootRuntime(
                    watchRoot,
                    sourceFactory,
                    observationPort,
                    destinationInspector,
                    planner,
                    delay,
                    timeProvider,
                    options))
            .ToArray();
        _rootsById = _roots.ToDictionary(static root => root.Configuration.Id);
        foreach (RootRuntime root in _roots)
        {
            root.Monitor.Notification += OnMonitorNotification;
            root.Coordinator.PreviewProduced += OnPreviewProduced;
        }

        _notificationSignals = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });
        _notificationProcessor = Task.Run(ProcessNotificationsAsync);
    }

    public event EventHandler<WatchPreviewRuntimeNotification>? SnapshotChanged;

    /// <summary>Gets an immutable historical snapshot in exact configuration order.</summary>
    public WatchPreviewRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return BuildSnapshotLocked();
            }
        }
    }

    /// <summary>Gets the one immutable configuration used by every owned root pipeline.</summary>
    public OrganizationConfiguration Configuration => _configuration;

    /// <summary>Starts each configured root in deterministic configuration order.</summary>
    public void Start()
    {
        _lifecycle.Wait();
        try
        {
            lock (_gate)
            {
                ThrowIfDisposalRequested();
                if (_started)
                {
                    return;
                }

                _started = true;
            }

            foreach (RootRuntime root in _roots)
            {
                root.Coordinator.Start();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Pauses one known started root and disposes its physical change source.</summary>
    public bool PauseRoot(Guid watchRootId) =>
        InvokeRootCommand(
            watchRootId,
            static root =>
            {
                if (root.Monitor.State == WatchRootState.Paused)
                {
                    return false;
                }

                root.Monitor.Pause();
                return true;
            });

    /// <summary>Resumes one enabled paused root; a fresh baseline remains mandatory.</summary>
    public bool ResumeRoot(Guid watchRootId) =>
        InvokeRootCommand(watchRootId, static root => root.Monitor.Resume());

    /// <summary>
    /// Reopens one enabled failed source. Incremental events remain untrusted until a fresh
    /// full-root baseline succeeds.
    /// </summary>
    public bool ReconnectRoot(Guid watchRootId) =>
        InvokeRootCommand(
            watchRootId,
            static root =>
            {
                if (!root.Configuration.IsEnabled
                    || root.Monitor.State is not (WatchRootState.Faulted
                        or WatchRootState.Unavailable))
                {
                    return false;
                }

                root.Monitor.Pause();
                return root.Monitor.Resume();
            });

    /// <summary>Explicitly retries a full baseline for one root awaiting synchronization.</summary>
    public bool RequestBaselineScan(Guid watchRootId) =>
        InvokeRootCommand(
            watchRootId,
            static root => root.Coordinator.RequestBaselineScan());

    /// <summary>Drains accepted monitor, preview, and snapshot-notification work.</summary>
    public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
    {
        Task? disposal;
        bool started;
        lock (_gate)
        {
            disposal = _disposeTask;
            started = _started;
        }

        if (disposal is not null)
        {
            await disposal.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!started)
        {
            return;
        }

        for (int pass = 0; pass < 2; pass++)
        {
            foreach (RootRuntime root in _roots)
            {
                await root.Monitor.DrainAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (RootRuntime root in _roots)
            {
                await root.Coordinator.DrainAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await DrainNotificationsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Joins one idempotent asynchronous shutdown of all owned pipelines.</summary>
    public ValueTask DisposeAsync()
    {
        Task disposal;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposeRequested = true;
                _disposeTask = DisposeCoreAsync();
            }

            disposal = _disposeTask;
        }

        return new ValueTask(disposal);
    }

    private RootRuntime CreateRootRuntime(
        WatchRootConfiguration configuration,
        IFileChangeSourceFactory sourceFactory,
        IFileStabilityObservationPort observationPort,
        IDestinationStateInspector destinationInspector,
        OrganizationPlanner planner,
        IWatchDelay delay,
        TimeProvider timeProvider,
        WatchPreviewRuntimeOptions options)
    {
        WatchRootMonitor monitor = new(
            configuration,
            sourceFactory,
            observationPort,
            delay,
            timeProvider,
            options.DebounceWindow,
            options.StabilityInterval);
        WatchRootPreviewCoordinator coordinator = new(
            monitor,
            _configuration,
            _scanner,
            destinationInspector,
            planner);
        return new RootRuntime(configuration, monitor, coordinator);
    }

    private bool InvokeRootCommand(
        Guid watchRootId,
        Func<RootRuntime, bool> command)
    {
        if (watchRootId == Guid.Empty)
        {
            return false;
        }

        _lifecycle.Wait();
        try
        {
            RootRuntime? root;
            lock (_gate)
            {
                if (!_started
                    || _disposeRequested
                    || !_rootsById.TryGetValue(watchRootId, out root))
                {
                    return false;
                }
            }

            try
            {
                return command(root);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void OnMonitorNotification(object? sender, WatchRootNotification notification)
    {
        if (sender is not WatchRootMonitor monitor
            || monitor.WatchRootId != notification.WatchRootId)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposeRequested
                || !_rootsById.TryGetValue(notification.WatchRootId, out RootRuntime? root))
            {
                return;
            }

            switch (notification.Kind)
            {
                case WatchRootNotificationKind.StateChanged:
                    root.State = notification.State!.Value;
                    break;
                case WatchRootNotificationKind.Diagnostic:
                    root.LatestDiagnostic = notification.Diagnostic;
                    break;
                case WatchRootNotificationKind.Candidate:
                case WatchRootNotificationKind.RefreshRequested:
                    return;
                default:
                    return;
            }

            RecordChangedLocked(root);
        }
    }

    private void OnPreviewProduced(object? sender, WatchRootPreviewResult preview)
    {
        if (sender is not WatchRootPreviewCoordinator coordinator
            || coordinator.WatchRootId != preview.WatchRootId)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposeRequested
                || !_rootsById.TryGetValue(preview.WatchRootId, out RootRuntime? root))
            {
                return;
            }

            root.LatestPreview = preview;
            RecordChangedLocked(root);
        }
    }

    private void RecordChangedLocked(RootRuntime root)
    {
        root.Revision = checked(root.Revision + 1);
        _revision = checked(_revision + 1);
        _changedRootIds.Add(root.Configuration.Id);
        if (_notificationIdle.Task.IsCompleted)
        {
            _notificationIdle = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _notificationSignals.Writer.TryWrite(true);
    }

    private async Task ProcessNotificationsAsync()
    {
        CancellationToken cancellationToken = _notificationLifetime.Token;
        try
        {
            await foreach (bool _ in _notificationSignals.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                WatchPreviewRuntimeNotification? notification;
                lock (_gate)
                {
                    if (_disposeRequested || _changedRootIds.Count == 0)
                    {
                        notification = null;
                    }
                    else
                    {
                        Guid[] changedIds = _roots
                            .Where(root => _changedRootIds.Contains(root.Configuration.Id))
                            .Select(static root => root.Configuration.Id)
                            .ToArray();
                        _changedRootIds.Clear();
                        _notificationDispatching = true;
                        notification = new WatchPreviewRuntimeNotification(
                            BuildSnapshotLocked(),
                            changedIds);
                    }
                }

                if (notification is null)
                {
                    CompleteNotificationIfIdle();
                    continue;
                }

                PublishSnapshot(notification);
                lock (_gate)
                {
                    _notificationDispatching = false;
                }

                CompleteNotificationIfIdle();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                _notificationDispatching = false;
                _notificationIdle.TrySetResult();
            }
        }
    }

    private void PublishSnapshot(WatchPreviewRuntimeNotification notification)
    {
        EventHandler<WatchPreviewRuntimeNotification>? handlers = SnapshotChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<WatchPreviewRuntimeNotification> handler
                 in handlers.GetInvocationList())
        {
            try
            {
                handler(this, notification);
            }
            catch (Exception)
            {
                // One presentation subscriber cannot break other observers or watcher callbacks.
            }
        }
    }

    private void CompleteNotificationIfIdle()
    {
        lock (_gate)
        {
            if (!_notificationDispatching && _changedRootIds.Count == 0)
            {
                _notificationIdle.TrySetResult();
            }
        }
    }

    private async ValueTask DrainNotificationsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task idle;
            lock (_gate)
            {
                idle = _notificationIdle.Task;
            }

            await idle.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_notificationDispatching && _changedRootIds.Count == 0)
                {
                    return;
                }
            }
        }
    }

    private WatchPreviewRuntimeSnapshot BuildSnapshotLocked() =>
        new(
            _revision,
            _roots.Select(
                static root => new WatchPreviewRootSnapshot(
                    root.Configuration.Id,
                    root.Configuration.RootPath,
                    root.Configuration.IsEnabled,
                    root.Configuration.IncludeSubdirectories,
                    root.State,
                    root.Revision,
                    root.LatestDiagnostic,
                    root.LatestPreview)));

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                foreach (RootRuntime root in _roots)
                {
                    root.Monitor.Notification -= OnMonitorNotification;
                    root.Coordinator.PreviewProduced -= OnPreviewProduced;
                }

                _changedRootIds.Clear();
                _notificationIdle.TrySetResult();
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        _notificationSignals.Writer.TryComplete();
        _notificationLifetime.Cancel();

        Exception? failure = null;
        try
        {
            await Task.WhenAll(
                    _roots.Select(static root => root.Coordinator.DisposeAsync().AsTask()))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await Task.WhenAll(
                    _roots.Select(static root => root.Monitor.DisposeAsync().AsTask()))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }

        try
        {
            await _notificationProcessor.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }
        finally
        {
            SnapshotChanged = null;
            _scanner.Dispose();
            _notificationLifetime.Dispose();
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void ThrowIfDisposalRequested()
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
    }

    private static Exception Combine(Exception? first, Exception second) =>
        first is null ? second : new AggregateException(first, second);

    private static TaskCompletionSource CompletedSignal()
    {
        TaskCompletionSource signal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private sealed class RootRuntime
    {
        public RootRuntime(
            WatchRootConfiguration configuration,
            WatchRootMonitor monitor,
            WatchRootPreviewCoordinator coordinator)
        {
            Configuration = configuration;
            Monitor = monitor;
            Coordinator = coordinator;
            State = configuration.IsEnabled
                ? WatchRootState.RequiresInitialScan
                : WatchRootState.Paused;
        }

        public WatchRootConfiguration Configuration { get; }

        public WatchRootMonitor Monitor { get; }

        public WatchRootPreviewCoordinator Coordinator { get; }

        public WatchRootState State { get; set; }

        public long Revision { get; set; }

        public WatchDiagnostic? LatestDiagnostic { get; set; }

        public WatchRootPreviewResult? LatestPreview { get; set; }
    }

    private sealed class ConcurrencyLimitedFileScanner : IFileScanner, IDisposable
    {
        private readonly IFileScanner _inner;
        private readonly SemaphoreSlim _slots;

        public ConcurrencyLimitedFileScanner(IFileScanner inner, int maxConcurrentScans)
        {
            _inner = inner;
            _slots = new SemaphoreSlim(maxConcurrentScans, maxConcurrentScans);
        }

        public FileScanResult Scan(
            CanonicalPath sourceRoot,
            bool recursive,
            CancellationToken cancellationToken = default)
        {
            _slots.Wait(cancellationToken);
            try
            {
                return _inner.Scan(sourceRoot, recursive, cancellationToken);
            }
            finally
            {
                _slots.Release();
            }
        }

        public void Dispose() => _slots.Dispose();
    }
}
