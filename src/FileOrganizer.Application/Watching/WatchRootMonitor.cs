using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Watching;

/// <summary>
/// Serializes untrusted watcher hints and emits only files proven stable by two handle observations.
/// </summary>
public sealed class WatchRootMonitor : IDisposable, IAsyncDisposable
{
    private const int MessageCapacity = 512;
    private const int TrackedPathCapacity = 256;
    private const int ActiveWorkCapacity = 512;

    private readonly object _gate = new();
    private readonly object _outputGate = new();
    private readonly WatchRootConfiguration _configuration;
    private readonly IFileChangeSourceFactory _sourceFactory;
    private readonly IFileStabilityObservationPort _observationPort;
    private readonly IWatchDelay _delay;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _debounceWindow;
    private readonly TimeSpan _stabilityInterval;
    private readonly PathPolicy _pathPolicy;
    private readonly Channel<IMonitorMessage> _messages;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<CanonicalPath, PendingPath> _pending;
    private readonly Dictionary<CanonicalPath, long> _pathGenerations;
    private readonly Dictionary<CanonicalPath, ObservedVersion> _lastEmitted;
    private readonly HashSet<Task> _activeWork = [];
    private readonly HashSet<Task> _drainableWork = [];
    private readonly HashSet<Task> _startingWork = [];
    private readonly Queue<WatchRootNotification> _pendingNotifications = [];
    private readonly Task _processor;

    private IFileChangeSource? _source;
    private EventHandler<FileChangeSourceEvent>? _sourceHandler;
    private WatchRootState _state;
    private long _activeSourceSession;
    private long _nextSourceSession;
    private long _lifecycleGeneration;
    private long _baselineEventGeneration;
    private long _pipelineEpoch;
    private bool _dispatchingNotifications;
    private bool _started;
    private bool _sourceStarted;
    private bool _disposed;

    public WatchRootMonitor(
        WatchRootConfiguration configuration,
        IFileChangeSourceFactory sourceFactory,
        IFileStabilityObservationPort observationPort,
        IWatchDelay delay,
        TimeProvider timeProvider,
        TimeSpan debounceWindow,
        TimeSpan stabilityInterval)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        ArgumentNullException.ThrowIfNull(observationPort);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            debounceWindow,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            stabilityInterval,
            TimeSpan.Zero);

        _configuration = configuration;
        _sourceFactory = sourceFactory;
        _observationPort = observationPort;
        _delay = delay;
        _timeProvider = timeProvider;
        _debounceWindow = debounceWindow;
        _stabilityInterval = stabilityInterval;
        _pathPolicy = new PathPolicy(
            configuration.RootPath.Semantics.Syntax,
            configuration.RootPath.Semantics.CaseSensitivity,
            configuration.RootPath.Semantics.UnicodeNormalization);
        _pending = new Dictionary<CanonicalPath, PendingPath>(_pathPolicy.LogicalComparer);
        _pathGenerations = new Dictionary<CanonicalPath, long>(_pathPolicy.LogicalComparer);
        _lastEmitted = new Dictionary<CanonicalPath, ObservedVersion>(_pathPolicy.LogicalComparer);
        _messages = Channel.CreateBounded<IMonitorMessage>(
            new BoundedChannelOptions(MessageCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        _state = configuration.IsEnabled
            ? WatchRootState.RequiresInitialScan
            : WatchRootState.Paused;
        _processor = Task.Run(ProcessMessagesAsync);
    }

    public event EventHandler<WatchRootNotification>? Notification;

    public Guid WatchRootId => _configuration.Id;

    /// <summary>Gets the immutable canonical root represented by this monitor.</summary>
    public CanonicalPath RootPath => _configuration.RootPath;

    /// <summary>Gets whether this monitor observes ordinary descendants recursively.</summary>
    public bool IncludeSubdirectories => _configuration.IncludeSubdirectories;

    /// <summary>Gets whether this monitor's immutable configuration enables the root.</summary>
    public bool IsEnabled => _configuration.IsEnabled;

    public WatchRootState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Starts a fresh physical source. Initial events remain untrusted.</summary>
    public void Start()
    {
        long lifecycle;
        bool disabled;
        lock (_outputGate)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_started)
                {
                    return;
                }

                _started = true;
                lifecycle = ++_lifecycleGeneration;
                disabled = !_configuration.IsEnabled;
            }

            if (disabled)
            {
                PublishCore(
                    WatchRootNotification.StateChanged(WatchRootId, WatchRootState.Paused));
                return;
            }
        }

        OpenAndStartSource(lifecycle, WatchRootState.RequiresInitialScan);
    }

    /// <summary>Stops physical watching and discards all pending candidates.</summary>
    public void Pause()
    {
        SourceLease? lease;
        bool changed;
        bool deferSourceDisposal;
        lock (_outputGate)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                ++_lifecycleGeneration;
                ++_baselineEventGeneration;
                ++_pipelineEpoch;
                lease = DetachSourceLocked();
                ClearTrackingLocked();
                changed = SetStateLocked(WatchRootState.Paused);
                deferSourceDisposal = _dispatchingNotifications;
            }

            if (changed)
            {
                PublishCore(
                    WatchRootNotification.StateChanged(WatchRootId, WatchRootState.Paused));
            }
        }

        DisposeDetachedSource(lease, deferSourceDisposal);
    }

    /// <summary>Starts a new source after pause; a new full baseline is mandatory.</summary>
    public bool Resume()
    {
        long lifecycle;
        bool deferSourceStart;
        lock (_outputGate)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!_configuration.IsEnabled || _state != WatchRootState.Paused)
                {
                    return false;
                }

                _started = true;
                lifecycle = ++_lifecycleGeneration;
                ++_baselineEventGeneration;
                ++_pipelineEpoch;
                ClearTrackingLocked();
                SetStateLocked(WatchRootState.RescanRequired);
                deferSourceStart = _dispatchingNotifications;
            }
        }

        if (deferSourceStart)
        {
            ScheduleEssentialTrackedWork(
                () =>
                {
                    OpenAndStartSource(lifecycle, WatchRootState.RescanRequired);
                    return Task.CompletedTask;
                });
        }
        else
        {
            OpenAndStartSource(lifecycle, WatchRootState.RescanRequired);
        }

        return true;
    }

    /// <summary>Captures the event generation immediately before a full baseline scan.</summary>
    public WatchBaselineToken CaptureBaselineToken()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_source is null
                || !_sourceStarted
                || _state is not (WatchRootState.RequiresInitialScan
                    or WatchRootState.RescanRequired))
            {
                throw new InvalidOperationException(
                    "A baseline can be captured only while an active source awaits resynchronization.");
            }

            return new WatchBaselineToken(WatchRootId, _baselineEventGeneration);
        }
    }

    /// <summary>
    /// Accepts a completed baseline only when no watcher event arrived since its token was captured.
    /// </summary>
    public bool AcknowledgeBaseline(WatchBaselineToken token) =>
        AcknowledgeBaselineAndCaptureSnapshot(token, out _);

    /// <summary>
    /// Atomically accepts an unchanged baseline and captures its synchronized generation.
    /// </summary>
    public bool AcknowledgeBaselineAndCaptureSnapshot(
        WatchBaselineToken token,
        [NotNullWhen(true)] out WatchSnapshotToken? snapshotToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        WatchRootNotification? stateNotification = null;
        WatchRootNotification? diagnosticNotification = null;
        bool accepted;
        snapshotToken = null;
        lock (_outputGate)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                accepted = token.WatchRootId == WatchRootId
                    && token.EventGeneration == _baselineEventGeneration
                    && _source is not null
                    && _sourceStarted
                    && _state is WatchRootState.RequiresInitialScan or WatchRootState.RescanRequired;
                ++_pipelineEpoch;
                ClearTrackingLocked();
                if (accepted)
                {
                    snapshotToken = new WatchSnapshotToken(
                        WatchRootId,
                        _baselineEventGeneration,
                        _pipelineEpoch);
                    if (SetStateLocked(WatchRootState.Synchronized))
                    {
                        stateNotification = WatchRootNotification.StateChanged(
                            WatchRootId,
                            WatchRootState.Synchronized);
                    }
                }
                else
                {
                    if (_source is not null && SetStateLocked(WatchRootState.RescanRequired))
                    {
                        stateNotification = WatchRootNotification.StateChanged(
                            WatchRootId,
                            WatchRootState.RescanRequired);
                    }

                    diagnosticNotification = WatchRootNotification.DiagnosticRaised(
                        WatchRootId,
                        new WatchDiagnostic(
                            WatchDiagnosticKind.BaselineMismatch,
                            "The baseline token no longer matches the watch-root event generation."));
                }
            }

            if (diagnosticNotification is not null && stateNotification is not null)
            {
                PublishCore(diagnosticNotification, stateNotification);
            }
            else if (diagnosticNotification is not null)
            {
                PublishCore(diagnosticNotification);
            }
            else if (stateNotification is not null)
            {
                PublishCore(stateNotification);
            }
        }

        return accepted;
    }

    /// <summary>Captures the current synchronized generation without changing monitor state.</summary>
    public bool TryCaptureSynchronizedSnapshot(
        [NotNullWhen(true)] out WatchSnapshotToken? snapshotToken)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_source is null
                || !_sourceStarted
                || _state != WatchRootState.Synchronized)
            {
                snapshotToken = null;
                return false;
            }

            snapshotToken = new WatchSnapshotToken(
                WatchRootId,
                _baselineEventGeneration,
                _pipelineEpoch);
            return true;
        }
    }

    /// <summary>Reports whether a synchronized snapshot token still names current root state.</summary>
    public bool IsSnapshotCurrent(WatchSnapshotToken snapshotToken)
    {
        ArgumentNullException.ThrowIfNull(snapshotToken);
        lock (_gate)
        {
            return IsSnapshotCurrentLocked(snapshotToken);
        }
    }

    /// <summary>
    /// Invalidates one still-current synchronized snapshot after read-only work finds uncertainty.
    /// </summary>
    public bool RequireRescan(
        WatchSnapshotToken snapshotToken,
        WatchDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(snapshotToken);
        ArgumentNullException.ThrowIfNull(diagnostic);

        bool changed;
        lock (_outputGate)
        {
            lock (_gate)
            {
                if (!IsSnapshotCurrentLocked(snapshotToken))
                {
                    return false;
                }

                changed = TransitionToRescanLocked();
            }

            PublishRescanNotifications(diagnostic, affectedPath: null, changed);
        }

        return true;
    }

    /// <summary>
    /// Atomically invalidates a live synchronized root so a newly attached
    /// preview coordinator can establish its own full-root baseline.
    /// </summary>
    /// <remarks>
    /// This startup-only boundary intentionally has no stale-work token.
    /// Asynchronous consumers must use the token-fenced overload instead.
    /// </remarks>
    internal bool RequireCoordinatorBaseline(WatchDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        bool changed;
        lock (_outputGate)
        {
            lock (_gate)
            {
                if (_disposed
                    || _source is null
                    || !_sourceStarted
                    || _state != WatchRootState.Synchronized)
                {
                    return false;
                }

                changed = TransitionToRescanLocked();
            }

            PublishRescanNotifications(diagnostic, affectedPath: null, changed);
        }

        return true;
    }

    /// <summary>Waits until all source callbacks queued before this call have been classified.</summary>
    public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await _messages.Writer.WriteAsync(new BarrierMessage(barrier), cancellationToken)
                .ConfigureAwait(false);
            Task completed = await Task.WhenAny(barrier.Task, _processor)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (completed == _processor)
            {
                return;
            }

            await barrier.Task.ConfigureAwait(false);

            Task[] startingWork;
            Task[] drainableWork;
            lock (_gate)
            {
                startingWork = [.. _startingWork];
                drainableWork = [.. _drainableWork];
            }

            if (startingWork.Length > 0)
            {
                await Task.WhenAll(startingWork)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            if (drainableWork.Length > 0)
            {
                await Task.WhenAll(drainableWork)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            await Task.Yield();
        }
    }

    public void Dispose()
    {
        SourceLease? lease;
        bool deferSourceDisposal;
        lock (_outputGate)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                ++_lifecycleGeneration;
                ++_pipelineEpoch;
                lease = DetachSourceLocked();
                ClearTrackingLocked();
                deferSourceDisposal = _dispatchingNotifications;
            }

            Notification = null;
            _pendingNotifications.Clear();
        }

        DisposeDetachedSource(lease, deferSourceDisposal);
        _lifetime.Cancel();
        _messages.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        try
        {
            await _processor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        while (true)
        {
            Task[] activeWork;
            lock (_gate)
            {
                activeWork = [.. _activeWork];
            }

            if (activeWork.Length == 0)
            {
                break;
            }

            await Task.WhenAll(activeWork).ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }

    private void OpenAndStartSource(long lifecycle, WatchRootState successfulState)
    {
        FileChangeSourceOpenResult result;
        try
        {
            result = _sourceFactory.Open(
                new FileChangeSourceRequest(
                    WatchRootId,
                    _configuration.RootPath,
                    _configuration.IncludeSubdirectories));
        }
        catch (Exception)
        {
            HandleSourceOpenFailure(
                lifecycle,
                WatchRootState.Faulted,
                new WatchDiagnostic(
                    WatchDiagnosticKind.InputOutputFailure,
                    "The physical watch source could not be opened."));
            return;
        }

        if (result.Status != FileChangeSourceOpenStatus.Opened)
        {
            WatchDiagnosticKind kind = result.Status == FileChangeSourceOpenStatus.Unsupported
                ? WatchDiagnosticKind.UnsupportedCapability
                : result.Problem!.Kind == FileChangeSourceProblemKind.Inaccessible
                    ? WatchDiagnosticKind.SourceInaccessible
                    : WatchDiagnosticKind.SourceUnavailable;
            HandleSourceOpenFailure(
                lifecycle,
                WatchRootState.Unavailable,
                new WatchDiagnostic(
                    kind,
                    result.Problem!.Description,
                    result.Problem.NativeErrorCode));
            return;
        }

        IFileChangeSource source = result.Source!;
        EventHandler<FileChangeSourceEvent>? handler = null;
        long session;
        bool installed;
        lock (_outputGate)
        {
            lock (_gate)
            {
                installed = !_disposed && lifecycle == _lifecycleGeneration && _source is null;
                if (installed)
                {
                    session = checked(++_nextSourceSession);
                    handler = (_, change) => ReceiveSourceEvent(session, change);
                    source.EventReceived += handler;
                    _source = source;
                    _sourceHandler = handler;
                    _activeSourceSession = session;
                    _sourceStarted = false;
                }
                else
                {
                    session = 0;
                }
            }
        }

        if (!installed)
        {
            source.Dispose();
            return;
        }

        try
        {
            source.Start();
        }
        catch (Exception)
        {
            HandleActiveSourceFailure(
                session,
                WatchRootState.Faulted,
                new WatchDiagnostic(
                    WatchDiagnosticKind.InputOutputFailure,
                    "The physical watch source could not be started."));
            return;
        }

        bool activated;
        lock (_outputGate)
        {
            lock (_gate)
            {
                activated = !_disposed
                    && lifecycle == _lifecycleGeneration
                    && session == _activeSourceSession
                    && ReferenceEquals(source, _source);
                if (activated)
                {
                    _sourceStarted = true;
                    SetStateLocked(successfulState);
                }
            }

            if (activated)
            {
                PublishCore(
                    WatchRootNotification.StateChanged(WatchRootId, successfulState));
            }
        }
    }

    private void HandleSourceOpenFailure(
        long lifecycle,
        WatchRootState state,
        WatchDiagnostic diagnostic)
    {
        bool publish;
        lock (_outputGate)
        {
            lock (_gate)
            {
                publish = !_disposed && lifecycle == _lifecycleGeneration;
                if (publish)
                {
                    ++_pipelineEpoch;
                    ClearTrackingLocked();
                    SetStateLocked(state);
                }
            }

            if (publish)
            {
                PublishCore(
                    WatchRootNotification.DiagnosticRaised(WatchRootId, diagnostic),
                    WatchRootNotification.StateChanged(WatchRootId, state));
            }
        }
    }

    private void ReceiveSourceEvent(long session, FileChangeSourceEvent change)
    {
        try
        {
            CriticalSourceTransition? criticalTransition = null;
            lock (_outputGate)
            {
                lock (_gate)
                {
                    if (_disposed || session != _activeSourceSession)
                    {
                        return;
                    }

                    ++_baselineEventGeneration;
                    if (change.Kind == FileChangeSourceEventKind.Overflow)
                    {
                        bool canRestart = _sourceStarted;
                        criticalTransition = BeginCriticalSourceTransitionLocked(
                            canRestart
                                ? WatchRootState.RescanRequired
                                : WatchRootState.Faulted,
                            new WatchDiagnostic(
                                WatchDiagnosticKind.BufferOverflow,
                                change.Problem!.Description,
                                change.Problem.NativeErrorCode),
                            reopen: canRestart);
                    }
                    else if (change.Kind == FileChangeSourceEventKind.WatcherError)
                    {
                        (WatchRootState state, WatchDiagnostic diagnostic) =
                            ClassifyWatcherProblem(change.Problem!);
                        criticalTransition = BeginCriticalSourceTransitionLocked(
                            state,
                            diagnostic,
                            reopen: false);
                    }
                    else if (_sourceStarted && _state == WatchRootState.Synchronized)
                    {
                        MarkAffectedPendingArrivalsLocked(change);
                        RawEventMessage message = new(session, _pipelineEpoch, change);
                        if (!_messages.Writer.TryWrite(message))
                        {
                            criticalTransition = BeginCriticalSourceTransitionLocked(
                                WatchRootState.RescanRequired,
                                new WatchDiagnostic(
                                    WatchDiagnosticKind.BufferOverflow,
                                    "The bounded application watcher queue overflowed; incremental state is uncertain."),
                                reopen: true);
                        }
                    }
                }

                if (criticalTransition is not null)
                {
                    PublishCriticalSourceTransitionUnderOutput(criticalTransition);
                    ScheduleCriticalSourceCleanupUnderOutput(criticalTransition);
                }
            }
        }
        catch (Exception)
        {
            HandleActiveSourceFailure(
                session,
                WatchRootState.Faulted,
                new WatchDiagnostic(
                    WatchDiagnosticKind.InputOutputFailure,
                    "A watcher callback could not be classified safely."));
        }
    }

    private async Task ProcessMessagesAsync()
    {
        try
        {
            await foreach (IMonitorMessage message in _messages.Reader.ReadAllAsync(_lifetime.Token)
                               .ConfigureAwait(false))
            {
                switch (message)
                {
                    case RawEventMessage raw:
                        ProcessRawEvent(raw);
                        break;
                    case DelayElapsedMessage elapsed:
                        ProcessDelayElapsed(elapsed);
                        break;
                    case ObservationMessage observation:
                        ProcessObservation(observation);
                        break;
                    case PipelineFailureMessage failure:
                        HandlePendingPipelineFailure(
                            failure.Pending,
                            new WatchDiagnostic(
                                WatchDiagnosticKind.InputOutputFailure,
                                failure.Description));
                        break;
                    case BarrierMessage barrier:
                        barrier.Completion.TrySetResult();
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void ProcessRawEvent(RawEventMessage message)
    {
        lock (_gate)
        {
            if (!IsCurrentRawEventLocked(message))
            {
                return;
            }
        }

        FileChangeSourceEvent change = message.Change;
        switch (change.Kind)
        {
            case FileChangeSourceEventKind.Overflow:
                RestartSourceForRescan(
                    message.SourceSession,
                    new WatchDiagnostic(
                        WatchDiagnosticKind.BufferOverflow,
                        change.Problem!.Description,
                        change.Problem.NativeErrorCode));
                break;
            case FileChangeSourceEventKind.WatcherError:
                ProcessWatcherError(message.SourceSession, change.Problem!);
                break;
            case FileChangeSourceEventKind.Created:
            case FileChangeSourceEventKind.Changed:
                ProcessCreatedOrChanged(message, change.Path!);
                break;
            case FileChangeSourceEventKind.Deleted:
                ProcessDeleted(message, change.Path!);
                break;
            case FileChangeSourceEventKind.Renamed:
                ProcessRenamed(message, change.OldPath!, change.Path!);
                break;
        }
    }

    private void ProcessWatcherError(long session, FileChangeSourceProblem problem)
    {
        (WatchRootState state, WatchDiagnostic diagnostic) = ClassifyWatcherProblem(problem);
        HandleActiveSourceFailure(
            session,
            state,
            diagnostic);
    }

    private static (WatchRootState State, WatchDiagnostic Diagnostic) ClassifyWatcherProblem(
        FileChangeSourceProblem problem)
    {
        WatchRootState state = problem.Kind is FileChangeSourceProblemKind.Inaccessible
            or FileChangeSourceProblemKind.Unavailable
            or FileChangeSourceProblemKind.UnsupportedCapability
                ? WatchRootState.Unavailable
                : WatchRootState.Faulted;
        WatchDiagnosticKind kind = problem.Kind switch
        {
            FileChangeSourceProblemKind.Inaccessible => WatchDiagnosticKind.SourceInaccessible,
            FileChangeSourceProblemKind.UnsupportedCapability =>
                WatchDiagnosticKind.UnsupportedCapability,
            FileChangeSourceProblemKind.Unavailable => WatchDiagnosticKind.SourceUnavailable,
            _ => WatchDiagnosticKind.InputOutputFailure,
        };
        return (state, new WatchDiagnostic(kind, problem.Description, problem.NativeErrorCode));
    }

    private void ProcessCreatedOrChanged(RawEventMessage message, string rawPath)
    {
        PathScope scope = ResolvePath(rawPath, out CanonicalPath? path);
        if (scope == PathScope.Inside)
        {
            ScheduleCandidate(message, path!, requireScopeProof: true);
            return;
        }

        RequireRescan(message, PathScopeDiagnostic(scope));
    }

    private void ProcessDeleted(RawEventMessage message, string rawPath)
    {
        PathScope scope = ResolvePath(rawPath, out CanonicalPath? path);
        if (scope == PathScope.Inside)
        {
            bool knownRegular;
            bool ambiguousScope;
            lock (_gate)
            {
                if (!IsCurrentRawEventLocked(message))
                {
                    return;
                }

                knownRegular = IsTrackedFileLocked(path!);
                ambiguousScope = HasTrackedDescendantLocked(path!);
                RemoveTrackedSubtreeLocked(path!);
            }

            if (!knownRegular || ambiguousScope)
            {
                RequireRescan(
                    message,
                    new WatchDiagnostic(
                        WatchDiagnosticKind.DirectoryScopeChanged,
                        "An unproven deleted entry may have changed directory scope."),
                    path);
            }
            else
            {
                PublishRefreshIfCurrent(message, path!);
            }

            return;
        }

        if (scope is PathScope.Invalid or PathScope.Root)
        {
            RequireRescan(message, PathScopeDiagnostic(scope));
        }
    }

    private void ProcessRenamed(
        RawEventMessage message,
        string rawOldPath,
        string rawNewPath)
    {
        PathScope oldScope = ResolvePath(rawOldPath, out CanonicalPath? oldPath);
        PathScope newScope = ResolvePath(rawNewPath, out CanonicalPath? newPath);
        if (oldScope is PathScope.Invalid or PathScope.Root
            || newScope is PathScope.Invalid or PathScope.Root)
        {
            RequireRescan(
                message,
                new WatchDiagnostic(
                    WatchDiagnosticKind.UnknownRename,
                    "A rename scope could not be reconstructed safely."));
            return;
        }

        if (oldScope == PathScope.Inside && newScope == PathScope.Inside)
        {
            bool knownRegular;
            bool ambiguousScope;
            lock (_gate)
            {
                if (!IsCurrentRawEventLocked(message))
                {
                    return;
                }

                knownRegular = IsTrackedFileLocked(oldPath!);
                ambiguousScope = HasTrackedDescendantLocked(oldPath!)
                    || HasTrackedDescendantLocked(newPath!);
                RemoveTrackedSubtreeLocked(oldPath!);
                RemoveTrackedSubtreeLocked(newPath!);
            }

            if (!knownRegular || ambiguousScope)
            {
                RequireRescan(
                    message,
                    new WatchDiagnostic(
                        WatchDiagnosticKind.DirectoryScopeChanged,
                        "An unproven rename may have changed directory scope."));
                return;
            }

            ScheduleCandidate(message, newPath!, requireScopeProof: true);
            return;
        }

        if (oldScope != PathScope.Inside && newScope == PathScope.Inside)
        {
            bool ambiguousScope;
            lock (_gate)
            {
                if (!IsCurrentRawEventLocked(message))
                {
                    return;
                }

                ambiguousScope = HasTrackedDescendantLocked(newPath!);
                RemoveTrackedSubtreeLocked(newPath!);
            }

            if (ambiguousScope)
            {
                RequireRescan(
                    message,
                    new WatchDiagnostic(
                        WatchDiagnosticKind.DirectoryScopeChanged,
                        "A rename into the watch root changed tracked directory scope."));
                return;
            }

            ScheduleCandidate(message, newPath!, requireScopeProof: true);
            return;
        }

        if (oldScope == PathScope.Inside && newScope != PathScope.Inside)
        {
            bool knownRegular;
            bool ambiguousScope;
            lock (_gate)
            {
                if (!IsCurrentRawEventLocked(message))
                {
                    return;
                }

                knownRegular = IsTrackedFileLocked(oldPath!);
                ambiguousScope = HasTrackedDescendantLocked(oldPath!);
                RemoveTrackedSubtreeLocked(oldPath!);
            }

            if (!knownRegular || ambiguousScope)
            {
                RequireRescan(
                    message,
                    new WatchDiagnostic(
                        WatchDiagnosticKind.UnknownRename,
                        "An untracked entry was renamed out of the watch root."));
            }
            else
            {
                PublishRefreshIfCurrent(message, oldPath!);
            }
        }
    }

    private void ScheduleCandidate(
        RawEventMessage message,
        CanonicalPath path,
        bool requireScopeProof = false)
    {
        PendingPath? pending = null;
        bool saturated = false;
        lock (_gate)
        {
            if (!IsCurrentRawEventLocked(message))
            {
                return;
            }

            RemovePendingLocked(path);
            if (!_pathGenerations.TryGetValue(path, out long previous)
                && _pathGenerations.Count >= TrackedPathCapacity)
            {
                saturated = true;
            }
            else if (previous == long.MaxValue)
            {
                saturated = true;
            }
            else
            {
                long generation = previous + 1;
                _pathGenerations[path] = generation;
                CancellationTokenSource cancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                pending = new PendingPath(
                    path,
                    generation,
                    message.SourceSession,
                    message.PipelineEpoch,
                    requireScopeProof && !_lastEmitted.ContainsKey(path),
                    cancellation);
                _pending[path] = pending;
            }
        }

        if (saturated)
        {
            RequireRescan(
                message,
                new WatchDiagnostic(
                    WatchDiagnosticKind.BufferOverflow,
                    "The bounded per-root candidate tracker reached capacity."));
        }
        else
        {
            ScheduleDelay(pending!, DelayStage.Debounce, _debounceWindow);
        }
    }

    private void ScheduleDelay(PendingPath pending, DelayStage stage, TimeSpan duration)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool scheduled = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_activeWork.Count < ActiveWorkCapacity)
            {
                Task task = Task.Run(() => WaitAndPostAsync(pending, stage, duration, started));
                _activeWork.Add(task);
                _startingWork.Add(started.Task);
                ObserveWorkCompletion(task);
                scheduled = true;
            }
        }

        if (!scheduled)
        {
            RequireRescan(
                pending,
                new WatchDiagnostic(
                    WatchDiagnosticKind.BufferOverflow,
                    "The bounded watcher work queue reached capacity."),
                pending.Path);
            return;
        }

        _ = started.Task.ContinueWith(
            static (_, state) => ((WatchRootMonitor)state!).OnWorkStarted(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WaitAndPostAsync(
        PendingPath pending,
        DelayStage stage,
        TimeSpan duration,
        TaskCompletionSource started)
    {
        try
        {
            ValueTask delay = _delay.DelayAsync(duration, pending.Token);
            started.TrySetResult();
            await delay.ConfigureAwait(false);
            await _messages.Writer.WriteAsync(
                    new DelayElapsedMessage(pending, stage),
                    pending.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pending.Token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            PostPipelineFailure(pending, "A watcher delay operation failed.");
        }
        finally
        {
            started.TrySetResult();
        }
    }

    private void ProcessDelayElapsed(DelayElapsedMessage message)
    {
        PendingPath? pending = GetCurrentPending(message.Pending);
        if (pending is null)
        {
            return;
        }

        if (!TryScheduleTrackedWork(
                () => ObserveAndPostAsync(pending, message.Stage),
                includeInDrain: true))
        {
            RequireRescan(
                pending,
                new WatchDiagnostic(
                    WatchDiagnosticKind.BufferOverflow,
                    "The bounded watcher work queue reached capacity."),
                pending.Path);
        }
    }

    private async Task ObserveAndPostAsync(PendingPath pending, DelayStage stage)
    {
        try
        {
            PhysicalEntryObservation observation = await _observationPort.ObserveAsync(
                    pending.Path,
                    pending.Token)
                .ConfigureAwait(false);
            await _messages.Writer.WriteAsync(
                    new ObservationMessage(
                        pending,
                        stage,
                        observation),
                    pending.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pending.Token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            PostPipelineFailure(
                pending,
                "A physical stability observation failed unexpectedly.");
        }
    }

    private void ProcessObservation(ObservationMessage message)
    {
        PendingPath? pending = GetCurrentPending(message.Pending);
        if (pending is null)
        {
            return;
        }

        PhysicalEntryObservation observation = message.Observation;
        if (observation.Outcome != PhysicalEntryObservationOutcome.Success)
        {
            HandleObservationFailure(pending, ObservationDiagnostic(observation));
            return;
        }

        if (observation.EntryKind != PhysicalEntryKind.RegularFile)
        {
            RequireRescan(
                pending,
                new WatchDiagnostic(
                    WatchDiagnosticKind.DirectoryScopeChanged,
                    "A directory-scoped change cannot be reconstructed incrementally."),
                pending.Path);
            return;
        }

        ObservedVersion version = ObservedVersion.From(observation);
        if (message.Stage == DelayStage.Debounce)
        {
            lock (_gate)
            {
                if (IsCurrentPendingLocked(pending))
                {
                    pending.FirstObservation = version;
                    pending.FirstObservationArrivalRevision = pending.ArrivalRevision;
                    pending.RequiresScopeProof = false;
                }
                else
                {
                    return;
                }
            }

            ScheduleDelay(pending, DelayStage.Stability, _stabilityInterval);
            return;
        }

        StableFileCandidate? candidate = null;
        bool drifted;
        bool supersededByArrival;
        bool publicationDeferred = false;
        bool candidateReady = false;
        long arrivalRevision;
        lock (_gate)
        {
            if (!IsCurrentPendingLocked(pending) || pending.FirstObservation is null)
            {
                return;
            }

            arrivalRevision = pending.ArrivalRevision;
            supersededByArrival = pending.FirstObservationArrivalRevision != arrivalRevision;
            if (supersededByArrival)
            {
                return;
            }

            drifted = pending.FirstObservation != version;
            if (drifted)
            {
                pending.FirstObservation = version;
                pending.FirstObservationArrivalRevision = arrivalRevision;
            }
            else
            {
                publicationDeferred = HasOverlappingScopeProbeLocked(pending);
                if (!publicationDeferred)
                {
                    candidateReady = true;
                }
            }
        }

        if (candidateReady)
        {
            candidate = new StableFileCandidate(
                WatchRootId,
                pending.Path,
                observation.Identity!,
                observation.FileMetadata!,
                pending.Generation,
                _timeProvider.GetUtcNow());
        }

        if (drifted)
        {
            bool current = PublishDiagnosticIfCurrent(
                pending,
                arrivalRevision,
                WatchRootNotification.DiagnosticRaised(
                    WatchRootId,
                    new WatchDiagnostic(
                        WatchDiagnosticKind.Unstable,
                        "Physical identity or required metadata changed during stability checking."),
                    pending.Path));
            if (current)
            {
                ScheduleDelay(pending, DelayStage.Stability, _stabilityInterval);
            }
        }
        else if (publicationDeferred)
        {
            ScheduleDelay(pending, DelayStage.Stability, _stabilityInterval);
        }
        else if (candidate is not null)
        {
            CandidatePublicationStatus status = PublishCandidateIfTrusted(
                candidate,
                pending,
                version,
                arrivalRevision);
            if (status == CandidatePublicationStatus.Deferred)
            {
                ScheduleDelay(pending, DelayStage.Stability, _stabilityInterval);
            }
        }
    }

    private void PostPipelineFailure(PendingPath pending, string description)
    {
        if (pending.Token.IsCancellationRequested || GetCurrentPending(pending) is null)
        {
            return;
        }

        if (_messages.Writer.TryWrite(new PipelineFailureMessage(pending, description)))
        {
            return;
        }

        HandlePendingPipelineFailure(
            pending,
            new WatchDiagnostic(
                WatchDiagnosticKind.InputOutputFailure,
                "The bounded watcher pipeline could not retain a failure signal."));
    }

    private bool TryScheduleTrackedWork(Func<Task> operation, bool includeInDrain = false)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Task task;
        lock (_gate)
        {
            if (_disposed || _activeWork.Count >= ActiveWorkCapacity)
            {
                return false;
            }

            task = Task.Run(operation);
            _activeWork.Add(task);
            if (includeInDrain)
            {
                _drainableWork.Add(task);
            }
        }

        ObserveWorkCompletion(task);
        return true;
    }

    private void ObserveWorkCompletion(Task task)
    {
        _ = task.ContinueWith(
            static (completed, state) =>
                ((WatchRootMonitor)state!).OnWorkCompleted(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnWorkCompleted(Task task)
    {
        lock (_gate)
        {
            _activeWork.Remove(task);
            _drainableWork.Remove(task);
        }
    }

    private void OnWorkStarted()
    {
        lock (_gate)
        {
            _startingWork.RemoveWhere(static task => task.IsCompleted);
        }
    }

    private PendingPath? GetCurrentPending(PendingPath expected)
    {
        lock (_gate)
        {
            return !_disposed
                && _state == WatchRootState.Synchronized
                && expected.SourceSession == _activeSourceSession
                && expected.PipelineEpoch == _pipelineEpoch
                && _pending.TryGetValue(expected.Path, out PendingPath? pending)
                && ReferenceEquals(expected, pending)
                    ? pending
                    : null;
        }
    }

    private bool IsCurrentPendingLocked(PendingPath pending) =>
        !_disposed
        && _state == WatchRootState.Synchronized
        && pending.SourceSession == _activeSourceSession
        && pending.PipelineEpoch == _pipelineEpoch
        && _pending.TryGetValue(pending.Path, out PendingPath? current)
        && ReferenceEquals(current, pending);

    private bool IsCurrentRawEventLocked(RawEventMessage message) =>
        !_disposed
        && _sourceStarted
        && _state == WatchRootState.Synchronized
        && message.SourceSession == _activeSourceSession
        && message.PipelineEpoch == _pipelineEpoch;

    private bool IsSnapshotCurrentLocked(WatchSnapshotToken snapshotToken) =>
        !_disposed
        && snapshotToken.WatchRootId == WatchRootId
        && snapshotToken.EventGeneration == _baselineEventGeneration
        && snapshotToken.SynchronizationEpoch == _pipelineEpoch
        && _source is not null
        && _sourceStarted
        && _state == WatchRootState.Synchronized;

    private void PublishRefreshIfCurrent(
        RawEventMessage message,
        CanonicalPath affectedPath)
    {
        lock (_outputGate)
        {
            lock (_gate)
            {
                if (!IsCurrentRawEventLocked(message))
                {
                    return;
                }
            }

            PublishCore(
                WatchRootNotification.RefreshRequested(
                    WatchRootId,
                    affectedPath));
        }
    }

    private void MarkAffectedPendingArrivalsLocked(FileChangeSourceEvent change)
    {
        List<CanonicalPath> affectedPaths = [];
        bool affectsAll = false;
        if (change.Path is not null)
        {
            AddArrivalScope(change.Path, affectedPaths, ref affectsAll);
        }

        if (change.OldPath is not null)
        {
            AddArrivalScope(change.OldPath, affectedPaths, ref affectsAll);
        }

        if (affectedPaths.Count == 0
            && change.Kind is FileChangeSourceEventKind.Created
                or FileChangeSourceEventKind.Changed)
        {
            affectsAll = true;
        }

        foreach (PendingPath pending in _pending.Values)
        {
            if (affectsAll || affectedPaths.Any(path => PathsOverlap(path, pending.Path)))
            {
                pending.MarkArrival();
            }
        }
    }

    private void AddArrivalScope(
        string rawPath,
        List<CanonicalPath> affectedPaths,
        ref bool affectsAll)
    {
        PathScope scope = ResolvePath(rawPath, out CanonicalPath? path);
        if (scope is PathScope.Invalid or PathScope.Root)
        {
            affectsAll = true;
        }
        else if (scope == PathScope.Inside)
        {
            affectedPaths.Add(path!);
        }
    }

    private bool HasOverlappingScopeProbeLocked(PendingPath candidate)
    {
        foreach (PendingPath pending in _pending.Values)
        {
            if (!ReferenceEquals(pending, candidate)
                && pending.RequiresScopeProof
                && PathsOverlap(pending.Path, candidate.Path))
            {
                return true;
            }
        }

        return false;
    }

    private bool PathsOverlap(CanonicalPath left, CanonicalPath right) =>
        _pathPolicy.Relate(left, right) is PathRelationship.Same or PathRelationship.Descendant
        || _pathPolicy.Relate(right, left) == PathRelationship.Descendant;

    private PathScope ResolvePath(string rawPath, out CanonicalPath? path)
    {
        PathResolution resolution = _pathPolicy.CanonicalizeAbsolute(rawPath);
        if (!resolution.Succeeded)
        {
            path = null;
            return PathScope.Invalid;
        }

        path = resolution.Path!;
        PathRelationship relationship = _pathPolicy.Relate(_configuration.RootPath, path);
        if (relationship == PathRelationship.Same)
        {
            return PathScope.Root;
        }

        if (relationship != PathRelationship.Descendant)
        {
            return PathScope.Outside;
        }

        if (_configuration.IncludeSubdirectories)
        {
            return PathScope.Inside;
        }

        CanonicalPath? parent = _pathPolicy.GetParent(path);
        return parent is not null && _pathPolicy.AreEquivalent(parent, _configuration.RootPath)
            ? PathScope.Inside
            : PathScope.Outside;
    }

    private static WatchDiagnostic PathScopeDiagnostic(PathScope scope) => scope switch
    {
        PathScope.Invalid => new WatchDiagnostic(
            WatchDiagnosticKind.InvalidPath,
            "A watcher event contained a path that cannot be validated."),
        PathScope.Root => new WatchDiagnostic(
            WatchDiagnosticKind.DirectoryScopeChanged,
            "The watch-root directory itself changed."),
        _ => new WatchDiagnostic(
            WatchDiagnosticKind.OutsideRoot,
            "A watcher event resolved outside its configured root."),
    };

    private static WatchDiagnostic ObservationDiagnostic(PhysicalEntryObservation observation)
    {
        WatchDiagnosticKind kind = observation.Outcome switch
        {
            PhysicalEntryObservationOutcome.Missing => WatchDiagnosticKind.Missing,
            PhysicalEntryObservationOutcome.Inaccessible => WatchDiagnosticKind.Inaccessible,
            PhysicalEntryObservationOutcome.Unsupported
                when observation.Reason == PhysicalEntryObservationReason.ReparsePoint =>
                    WatchDiagnosticKind.ReparsePoint,
            PhysicalEntryObservationOutcome.Unsupported
                when observation.Reason == PhysicalEntryObservationReason.InvalidPath =>
                    WatchDiagnosticKind.InvalidPath,
            PhysicalEntryObservationOutcome.Unsupported
                when observation.Reason == PhysicalEntryObservationReason.UnsupportedCapability =>
                    WatchDiagnosticKind.UnsupportedCapability,
            _ => WatchDiagnosticKind.UnsupportedEntry,
        };
        string description = kind switch
        {
            WatchDiagnosticKind.Missing => "The candidate disappeared before it became stable.",
            WatchDiagnosticKind.Inaccessible => "The candidate could not be inspected safely.",
            WatchDiagnosticKind.ReparsePoint => "A reparse-point candidate is not trusted.",
            WatchDiagnosticKind.InvalidPath => "The candidate path cannot be observed safely.",
            WatchDiagnosticKind.UnsupportedCapability =>
                "Strong physical observation is unsupported on this host.",
            _ => "The candidate entry kind or capability is unsupported.",
        };
        return new WatchDiagnostic(kind, description, observation.NativeErrorCode);
    }

    private void HandleObservationFailure(PendingPath pending, WatchDiagnostic diagnostic)
    {
        bool requiresRescan;
        bool stateChanged = false;
        lock (_outputGate)
        {
            lock (_gate)
            {
                if (!IsCurrentPendingLocked(pending))
                {
                    return;
                }

                requiresRescan = pending.RequiresScopeProof;
                if (requiresRescan)
                {
                    stateChanged = TransitionToRescanLocked();
                }
                else
                {
                    RemovePendingLocked(pending.Path);
                }
            }

            if (requiresRescan)
            {
                PublishRescanNotifications(diagnostic, pending.Path, stateChanged);
            }
            else
            {
                PublishCore(
                    WatchRootNotification.DiagnosticRaised(
                        WatchRootId,
                        diagnostic,
                        pending.Path));
            }
        }
    }

    private bool PublishDiagnosticIfCurrent(
        PendingPath pending,
        long arrivalRevision,
        WatchRootNotification notification)
    {
        lock (_outputGate)
        {
            lock (_gate)
            {
                if (!IsCurrentPendingLocked(pending)
                    || pending.ArrivalRevision != arrivalRevision)
                {
                    return false;
                }
            }

            PublishCore(notification);
            return true;
        }
    }

    private void RestartSourceForRescan(long session, WatchDiagnostic diagnostic)
    {
        CriticalSourceTransition? transition;
        lock (_outputGate)
        {
            lock (_gate)
            {
                if (_disposed || session == 0 || session != _activeSourceSession)
                {
                    return;
                }

                transition = BeginCriticalSourceTransitionLocked(
                    WatchRootState.RescanRequired,
                    diagnostic,
                    reopen: true);
            }

            PublishCriticalSourceTransitionUnderOutput(transition);
            ScheduleCriticalSourceCleanupUnderOutput(transition);
        }
    }

    private CriticalSourceTransition BeginCriticalSourceTransitionLocked(
        WatchRootState state,
        WatchDiagnostic diagnostic,
        bool reopen)
    {
        long lifecycle = ++_lifecycleGeneration;
        ++_baselineEventGeneration;
        ++_pipelineEpoch;
        SourceLease? lease = DetachSourceLocked();
        ClearTrackingLocked();
        SetStateLocked(state);
        return new CriticalSourceTransition(lease, lifecycle, state, diagnostic, reopen);
    }

    private void PublishCriticalSourceTransitionUnderOutput(
        CriticalSourceTransition transition)
    {
        WatchRootNotification diagnostic =
            WatchRootNotification.DiagnosticRaised(WatchRootId, transition.Diagnostic);
        if (transition.Reopen)
        {
            PublishCore(diagnostic);
        }
        else
        {
            PublishCore(
                diagnostic,
                WatchRootNotification.StateChanged(WatchRootId, transition.State));
        }
    }

    private void ScheduleCriticalSourceCleanupUnderOutput(
        CriticalSourceTransition transition) =>
        ScheduleEssentialTrackedWork(
            () => CompleteCriticalSourceTransitionAsync(transition),
            includeInDrain: true);

    private void ScheduleEssentialTrackedWork(
        Func<Task> operation,
        bool includeInDrain = false)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Task task = Task.Run(operation);
        lock (_gate)
        {
            _activeWork.Add(task);
            if (includeInDrain)
            {
                _drainableWork.Add(task);
            }
        }

        ObserveWorkCompletion(task);
    }

    private Task CompleteCriticalSourceTransitionAsync(CriticalSourceTransition transition)
    {
        DisposeSource(transition.Lease);
        if (!transition.Reopen)
        {
            return Task.CompletedTask;
        }

        bool current;
        lock (_outputGate)
        {
            lock (_gate)
            {
                current = !_disposed
                    && transition.Lifecycle == _lifecycleGeneration
                    && _state == WatchRootState.RescanRequired
                    && _source is null;
            }
        }

        if (current)
        {
            OpenAndStartSource(transition.Lifecycle, WatchRootState.RescanRequired);
        }

        return Task.CompletedTask;
    }

    private void RequireRescan(
        RawEventMessage message,
        WatchDiagnostic diagnostic,
        CanonicalPath? affectedPath = null)
    {
        bool changed;
        lock (_outputGate)
        {
            lock (_gate)
            {
                if (!IsCurrentRawEventLocked(message))
                {
                    return;
                }

                changed = TransitionToRescanLocked();
            }

            PublishRescanNotifications(diagnostic, affectedPath, changed);
        }
    }

    private void RequireRescan(
        PendingPath pending,
        WatchDiagnostic diagnostic,
        CanonicalPath? affectedPath = null)
    {
        bool changed;
        lock (_outputGate)
        {
            lock (_gate)
            {
                if (!IsCurrentPendingLocked(pending))
                {
                    return;
                }

                changed = TransitionToRescanLocked();
            }

            PublishRescanNotifications(diagnostic, affectedPath, changed);
        }
    }

    private bool TransitionToRescanLocked()
    {
        ++_baselineEventGeneration;
        ++_pipelineEpoch;
        ClearTrackingLocked();
        return SetStateLocked(WatchRootState.RescanRequired);
    }

    private void PublishRescanNotifications(
        WatchDiagnostic diagnostic,
        CanonicalPath? affectedPath,
        bool stateChanged)
    {
        if (stateChanged)
        {
            PublishCore(
                WatchRootNotification.DiagnosticRaised(
                    WatchRootId,
                    diagnostic,
                    affectedPath),
                WatchRootNotification.StateChanged(
                    WatchRootId,
                    WatchRootState.RescanRequired));
        }
        else
        {
            PublishCore(
                WatchRootNotification.DiagnosticRaised(
                    WatchRootId,
                    diagnostic,
                    affectedPath));
        }
    }

    private void HandleActiveSourceFailure(
        long session,
        WatchRootState state,
        WatchDiagnostic diagnostic)
    {
        CriticalSourceTransition? transition;
        lock (_outputGate)
        {
            lock (_gate)
            {
                if (_disposed || session == 0 || session != _activeSourceSession)
                {
                    return;
                }

                transition = BeginCriticalSourceTransitionLocked(
                    state,
                    diagnostic,
                    reopen: false);
            }

            PublishCriticalSourceTransitionUnderOutput(transition);
            ScheduleCriticalSourceCleanupUnderOutput(transition);
        }
    }

    private void HandlePendingPipelineFailure(
        PendingPath pending,
        WatchDiagnostic diagnostic)
    {
        CriticalSourceTransition transition;
        lock (_outputGate)
        {
            lock (_gate)
            {
                if (!IsCurrentPendingLocked(pending))
                {
                    return;
                }

                transition = BeginCriticalSourceTransitionLocked(
                    WatchRootState.Faulted,
                    diagnostic,
                    reopen: false);
            }

            PublishCriticalSourceTransitionUnderOutput(transition);
            ScheduleCriticalSourceCleanupUnderOutput(transition);
        }
    }

    private bool RemoveTrackedPathLocked(CanonicalPath path)
    {
        bool pending = RemovePendingLocked(path);
        bool emitted = _lastEmitted.Remove(path);
        return pending || emitted;
    }

    private bool IsTrackedFileLocked(CanonicalPath path) =>
        _lastEmitted.ContainsKey(path)
        || (_pending.TryGetValue(path, out PendingPath? pending)
            && pending.FirstObservation is not null);

    private bool HasTrackedDescendantLocked(CanonicalPath path)
    {
        foreach (CanonicalPath candidate in _pending.Keys)
        {
            if (_pathPolicy.Relate(path, candidate) == PathRelationship.Descendant)
            {
                return true;
            }
        }

        foreach (CanonicalPath candidate in _lastEmitted.Keys)
        {
            if (_pathPolicy.Relate(path, candidate) == PathRelationship.Descendant)
            {
                return true;
            }
        }

        return false;
    }

    private void RemoveTrackedSubtreeLocked(CanonicalPath path)
    {
        HashSet<CanonicalPath> paths = new(_pathPolicy.LogicalComparer);
        foreach (CanonicalPath candidate in _pending.Keys)
        {
            PathRelationship relationship = _pathPolicy.Relate(path, candidate);
            if (relationship is PathRelationship.Same or PathRelationship.Descendant)
            {
                paths.Add(candidate);
            }
        }

        foreach (CanonicalPath candidate in _lastEmitted.Keys)
        {
            PathRelationship relationship = _pathPolicy.Relate(path, candidate);
            if (relationship is PathRelationship.Same or PathRelationship.Descendant)
            {
                paths.Add(candidate);
            }
        }

        foreach (CanonicalPath candidate in paths)
        {
            RemoveTrackedPathLocked(candidate);
        }
    }

    private bool RemovePendingLocked(CanonicalPath path)
    {
        if (!_pending.Remove(path, out PendingPath? pending))
        {
            return false;
        }

        pending.Cancel();
        return true;
    }

    private void ClearPendingLocked()
    {
        foreach (PendingPath pending in _pending.Values)
        {
            pending.Cancel();
        }

        _pending.Clear();
    }

    private void ClearTrackingLocked()
    {
        ClearPendingLocked();
        _pathGenerations.Clear();
        _lastEmitted.Clear();
    }

    private SourceLease? DetachSourceLocked()
    {
        if (_source is null)
        {
            _activeSourceSession = 0;
            _sourceHandler = null;
            _sourceStarted = false;
            return null;
        }

        SourceLease lease = new(_source, _sourceHandler!);
        _source = null;
        _sourceHandler = null;
        _activeSourceSession = 0;
        _sourceStarted = false;
        return lease;
    }

    private static void DisposeSource(SourceLease? lease)
    {
        if (lease is null)
        {
            return;
        }

        try
        {
            lease.Source.EventReceived -= lease.Handler;
        }
        catch (Exception)
        {
            // The detached session can no longer publish trusted events.
        }

        try
        {
            lease.Source.Dispose();
        }
        catch (Exception)
        {
            // Disposal is best effort after trust and callback ownership have been revoked.
        }
    }

    private void DisposeDetachedSource(SourceLease? lease, bool defer)
    {
        if (lease is null)
        {
            return;
        }

        if (defer)
        {
            ScheduleEssentialTrackedWork(
                () =>
                {
                    DisposeSource(lease);
                    return Task.CompletedTask;
                });
            return;
        }

        DisposeSource(lease);
    }

    private bool SetStateLocked(WatchRootState state)
    {
        bool changed = _state != state;
        _state = state;
        return changed;
    }

    private void Publish(WatchRootNotification? notification)
    {
        if (notification is null)
        {
            return;
        }

        lock (_outputGate)
        {
            PublishCore(notification);
        }
    }

    private CandidatePublicationStatus PublishCandidateIfTrusted(
        StableFileCandidate candidate,
        PendingPath pending,
        ObservedVersion version,
        long arrivalRevision)
    {
        lock (_outputGate)
        {
            bool duplicate;
            lock (_gate)
            {
                if (!IsCurrentPendingLocked(pending)
                    || pending.ArrivalRevision != arrivalRevision)
                {
                    return CandidatePublicationStatus.Stale;
                }

                if (HasOverlappingScopeProbeLocked(pending))
                {
                    return CandidatePublicationStatus.Deferred;
                }

                RemovePendingLocked(pending.Path);
                duplicate = _lastEmitted.TryGetValue(
                        pending.Path,
                        out ObservedVersion? previous)
                    && previous == version;
                _lastEmitted[pending.Path] = version;
            }

            if (!duplicate)
            {
                PublishCore(WatchRootNotification.CandidateReady(candidate));
            }

            return CandidatePublicationStatus.Published;
        }
    }

    private void PublishCore(params WatchRootNotification[] notifications)
    {
        foreach (WatchRootNotification notification in notifications)
        {
            _pendingNotifications.Enqueue(notification);
        }

        if (_dispatchingNotifications)
        {
            return;
        }

        _dispatchingNotifications = true;
        try
        {
            while (_pendingNotifications.TryDequeue(out WatchRootNotification? next))
            {
                EventHandler<WatchRootNotification>? handlers = Notification;
                if (handlers is null)
                {
                    continue;
                }

                foreach (EventHandler<WatchRootNotification> handler in handlers.GetInvocationList())
                {
                    DispatchSafely(handler, next);
                }
            }
        }
        finally
        {
            _dispatchingNotifications = false;
        }
    }

    private void DispatchSafely(
        EventHandler<WatchRootNotification> handler,
        WatchRootNotification notification)
    {
        try
        {
            handler(this, notification);
        }
        catch (Exception)
        {
            // One subscriber cannot break watcher callback or pipeline processing.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private interface IMonitorMessage;

    private sealed record RawEventMessage(
        long SourceSession,
        long PipelineEpoch,
        FileChangeSourceEvent Change)
        : IMonitorMessage;

    private sealed record DelayElapsedMessage(PendingPath Pending, DelayStage Stage)
        : IMonitorMessage;

    private sealed record ObservationMessage(
        PendingPath Pending,
        DelayStage Stage,
        PhysicalEntryObservation Observation) : IMonitorMessage;

    private sealed record PipelineFailureMessage(PendingPath Pending, string Description)
        : IMonitorMessage;

    private sealed record BarrierMessage(TaskCompletionSource Completion) : IMonitorMessage;

    private sealed class PendingPath(
        CanonicalPath path,
        long generation,
        long sourceSession,
        long pipelineEpoch,
        bool requiresScopeProof,
        CancellationTokenSource cancellation)
    {
        private readonly CancellationTokenSource _cancellation = cancellation;

        public CanonicalPath Path { get; } = path;

        public long Generation { get; } = generation;

        public long SourceSession { get; } = sourceSession;

        public long PipelineEpoch { get; } = pipelineEpoch;

        public CancellationToken Token { get; } = cancellation.Token;

        public bool RequiresScopeProof { get; set; } = requiresScopeProof;

        public ObservedVersion? FirstObservation { get; set; }

        public long FirstObservationArrivalRevision { get; set; }

        public long ArrivalRevision { get; private set; }

        public void MarkArrival() => ArrivalRevision = checked(ArrivalRevision + 1);

        public void Cancel()
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
        }
    }

    private sealed record ObservedVersion(
        PhysicalEntryIdentity Identity,
        long Length,
        DateTimeOffset ModifiedUtc)
    {
        public static ObservedVersion From(PhysicalEntryObservation observation) =>
            new(
                observation.Identity!,
                observation.FileMetadata!.Length,
                observation.FileMetadata.ModifiedUtc);
    }

    private sealed record SourceLease(
        IFileChangeSource Source,
        EventHandler<FileChangeSourceEvent> Handler);

    private sealed record CriticalSourceTransition(
        SourceLease? Lease,
        long Lifecycle,
        WatchRootState State,
        WatchDiagnostic Diagnostic,
        bool Reopen);

    private enum DelayStage
    {
        Debounce,
        Stability,
    }

    private enum CandidatePublicationStatus
    {
        Published,
        Deferred,
        Stale,
    }

    private enum PathScope
    {
        Invalid,
        Root,
        Inside,
        Outside,
    }
}
