using System.Threading.Channels;
using System.Diagnostics.CodeAnalysis;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Core.Abstractions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Application.Watching;

/// <summary>
/// Converts trusted watch notifications into serialized, full-root, read-only plan previews.
/// </summary>
/// <remarks>
/// This coordinator owns no execution capability. The supplied monitor remains caller-owned.
/// One active refresh and at most one latest pending refresh are retained per coordinator.
/// </remarks>
public sealed class WatchRootPreviewCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly WatchRootMonitor _monitor;
    private readonly WatchRootConfiguration _watchRoot;
    private readonly OrganizationRuleSet _rules;
    private readonly IFileScanner _scanner;
    private readonly IDestinationStateInspector _destinationInspector;
    private readonly OrganizationPlanner _planner;
    private readonly PathPolicy _pathPolicy;
    private readonly Channel<bool> _signals;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _processor;
    private readonly TaskCompletionSource _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private RefreshRequest? _pending;
    private RefreshRequest? _active;
    private PhysicalEntryIdentity? _acknowledgedRootIdentity;
    private TaskCompletionSource _idle = CompletedSignal();
    private bool _startingMonitor;
    private bool _startupBaselineNotificationSeen;
    private bool _started;
    private bool _disposed;

    public WatchRootPreviewCoordinator(
        WatchRootMonitor monitor,
        OrganizationConfiguration configuration,
        IFileScanner scanner,
        IDestinationStateInspector destinationInspector,
        OrganizationPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(destinationInspector);
        ArgumentNullException.ThrowIfNull(planner);

        WatchRootConfiguration? watchRoot = configuration.WatchRoots.SingleOrDefault(
            candidate => candidate.Id == monitor.WatchRootId);
        if (watchRoot is null)
        {
            throw new ArgumentException(
                "The configuration does not contain the monitor's watch-root ID.",
                nameof(configuration));
        }

        var pathPolicy = new PathPolicy(
            configuration.PathSemantics.Syntax,
            configuration.PathSemantics.CaseSensitivity,
            configuration.PathSemantics.UnicodeNormalization);
        if (monitor.RootPath.Semantics != configuration.PathSemantics
            || watchRoot.RootPath.Semantics != configuration.PathSemantics
            || !pathPolicy.AreEquivalent(monitor.RootPath, watchRoot.RootPath)
            || monitor.IncludeSubdirectories != watchRoot.IncludeSubdirectories
            || monitor.IsEnabled != watchRoot.IsEnabled)
        {
            throw new ArgumentException(
                "The monitor and configured watch root must describe the same logical scope.",
                nameof(configuration));
        }

        _monitor = monitor;
        _watchRoot = watchRoot;
        _rules = configuration.RuleSet;
        _scanner = scanner;
        _destinationInspector = destinationInspector;
        _planner = planner;
        _pathPolicy = pathPolicy;
        _signals = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });
        _processor = Task.Run(ProcessRequestsAsync);
    }

    /// <summary>Raised from the coordinator worker after a request reaches a final classification.</summary>
    public event EventHandler<WatchRootPreviewResult>? PreviewProduced;

    public Guid WatchRootId => _watchRoot.Id;

    /// <summary>Explicitly retries a failed full-root baseline without creating a retry loop.</summary>
    public bool RequestBaselineScan()
    {
        lock (_gate)
        {
            if (!_started || _disposed)
            {
                return false;
            }
        }

        WatchRootState state;
        try
        {
            state = _monitor.State;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return (state is WatchRootState.RequiresInitialScan or WatchRootState.RescanRequired)
            && Enqueue(RefreshRequest.ForBaseline());
    }

    /// <summary>Waits until every coordinator request accepted before this call is classified.</summary>
    public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_gate)
        {
            idle = _idle.Task;
        }

        await idle.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Subscribes to the monitor before starting it and requests any required baseline.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_started)
            {
                return;
            }

            _started = true;
            _startingMonitor = true;
            _startupBaselineNotificationSeen = false;
            _monitor.Notification += OnMonitorNotification;
        }

        try
        {
            _monitor.Start();
            WatchRootState state = _monitor.State;
            bool baselineNotificationSeen;
            lock (_gate)
            {
                baselineNotificationSeen = _startupBaselineNotificationSeen;
                _startingMonitor = false;
            }

            if (!baselineNotificationSeen
                && state == WatchRootState.Synchronized)
            {
                _monitor.RequireCoordinatorBaseline(
                    new WatchDiagnostic(
                        WatchDiagnosticKind.IncompleteBaseline,
                        "The preview coordinator requires its own full-root baseline."));
            }
            else if (!baselineNotificationSeen)
            {
                QueueBaselineIfRequired(state);
            }
        }
        catch
        {
            lock (_gate)
            {
                _monitor.Notification -= OnMonitorNotification;
                _started = false;
                _startingMonitor = false;
                ClearPendingLocked();
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        bool initiateDisposal;
        lock (_gate)
        {
            initiateDisposal = !_disposed;
            if (initiateDisposal)
            {
                _disposed = true;
                _pending = null;
                _idle.TrySetResult();
                if (_started)
                {
                    _monitor.Notification -= OnMonitorNotification;
                }

                _signals.Writer.TryComplete();
                _lifetime.Cancel();
            }
        }

        if (!initiateDisposal)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        Exception? failure = null;
        try
        {
            await _processor.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            PreviewProduced = null;
            _lifetime.Dispose();
            if (failure is null)
            {
                _disposeCompletion.TrySetResult();
            }
            else
            {
                _disposeCompletion.TrySetException(failure);
            }
        }
    }

    private void OnMonitorNotification(object? sender, WatchRootNotification notification)
    {
        try
        {
            if (notification.WatchRootId != WatchRootId)
            {
                return;
            }

            switch (notification.Kind)
            {
                case WatchRootNotificationKind.StateChanged:
                    if (notification.State is WatchRootState.RequiresInitialScan
                        or WatchRootState.RescanRequired)
                    {
                        lock (_gate)
                        {
                            if (_startingMonitor)
                            {
                                _startupBaselineNotificationSeen = true;
                            }
                        }
                    }

                    QueueBaselineIfRequired(
                        notification.State!.Value,
                        allowBehindActiveBaseline: true);
                    break;
                case WatchRootNotificationKind.Candidate:
                    Enqueue(RefreshRequest.ForCandidate(notification.Candidate!));
                    break;
                case WatchRootNotificationKind.RefreshRequested:
                    Enqueue(RefreshRequest.ForAffectedPath(notification.AffectedPath!));
                    break;
                case WatchRootNotificationKind.Diagnostic:
                    break;
                default:
                    throw new InvalidOperationException("Unknown watch-root notification kind.");
            }
        }
        catch (Exception)
        {
            // Watcher callbacks are untrusted process boundaries. Never let planning escape them.
        }
    }

    private void QueueBaselineIfRequired(
        WatchRootState state,
        bool allowBehindActiveBaseline = false)
    {
        if (state is WatchRootState.RequiresInitialScan or WatchRootState.RescanRequired)
        {
            Enqueue(RefreshRequest.ForBaseline(), allowBehindActiveBaseline);
            return;
        }

        if (state is WatchRootState.Paused or WatchRootState.Faulted or WatchRootState.Unavailable)
        {
            lock (_gate)
            {
                _acknowledgedRootIdentity = null;
                ClearPendingLocked();
            }
        }
    }

    private bool Enqueue(
        RefreshRequest request,
        bool allowBehindActiveBaseline = false)
    {
        lock (_gate)
        {
            if (!_started || _disposed)
            {
                return false;
            }

            if (_pending?.Trigger == WatchRootPreviewTrigger.Baseline
                && request.Trigger != WatchRootPreviewTrigger.Baseline)
            {
                return false;
            }

            if (request.Trigger == WatchRootPreviewTrigger.Baseline
                && _pending?.Trigger == WatchRootPreviewTrigger.Baseline)
            {
                return true;
            }

            if (request.Trigger == WatchRootPreviewTrigger.Baseline
                && _active?.Trigger == WatchRootPreviewTrigger.Baseline
                && !allowBehindActiveBaseline)
            {
                return true;
            }

            if (request.Trigger == WatchRootPreviewTrigger.Baseline)
            {
                _acknowledgedRootIdentity = null;
            }

            if (_idle.Task.IsCompleted)
            {
                _idle = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _pending = request;
            _signals.Writer.TryWrite(true);
            return true;
        }
    }

    private async Task ProcessRequestsAsync()
    {
        CancellationToken cancellationToken = _lifetime.Token;
        try
        {
            await foreach (bool _ in _signals.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                while (TryTakePending(out RefreshRequest? request))
                {
                    try
                    {
                        ProcessRequest(request, cancellationToken);
                    }
                    finally
                    {
                        CompleteRequest(request);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool TryTakePending([NotNullWhen(true)] out RefreshRequest? request)
    {
        lock (_gate)
        {
            if (_disposed || _pending is null)
            {
                request = null;
                return false;
            }

            request = _pending;
            _pending = null;
            _active = request;
            return true;
        }
    }

    private void CompleteRequest(RefreshRequest request)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_active, request))
            {
                _active = null;
            }

            if (_pending is null)
            {
                _idle.TrySetResult();
            }
        }
    }

    private void ClearPendingLocked()
    {
        _pending = null;
        if (_active is null)
        {
            _idle.TrySetResult();
        }
    }

    private void ProcessRequest(RefreshRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Trigger == WatchRootPreviewTrigger.Baseline)
        {
            ProcessBaseline(request, cancellationToken);
        }
        else
        {
            ProcessSynchronizedRefresh(request, cancellationToken);
        }
    }

    private void ProcessBaseline(RefreshRequest request, CancellationToken cancellationToken)
    {
        WatchBaselineToken baselineToken;
        try
        {
            baselineToken = _monitor.CaptureBaselineToken();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            PublishNotReady(
                request,
                WatchRootPreviewOutcome.Suppressed,
                WatchRootPreviewProblemKind.StaleSnapshot,
                "The monitor no longer requires this baseline.");
            return;
        }

        if (!TryCreatePlan(
                request,
                snapshotToken: null,
                cancellationToken,
                out FileScanResult? scan,
                out OrganizationPlan? plan))
        {
            return;
        }

        PhysicalEntryIdentity rootIdentity = scan.RootPhysicalIdentity!;
        WatchSnapshotToken? snapshotToken;
        try
        {
            if (!_monitor.AcknowledgeBaselineAndCaptureSnapshot(
                    baselineToken,
                    out snapshotToken))
            {
                PublishNotReady(
                    request,
                    WatchRootPreviewOutcome.Suppressed,
                    WatchRootPreviewProblemKind.StaleSnapshot,
                    "A filesystem event invalidated the baseline before publication.");
                Enqueue(
                    RefreshRequest.ForBaseline(),
                    allowBehindActiveBaseline: true);
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            PublishNotReady(
                request,
                WatchRootPreviewOutcome.Suppressed,
                WatchRootPreviewProblemKind.StaleSnapshot,
                "The monitor was disposed before baseline publication.");
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _acknowledgedRootIdentity = rootIdentity;
        }

        PublishReadyIfCurrent(request, snapshotToken, plan);
    }

    private void ProcessSynchronizedRefresh(
        RefreshRequest request,
        CancellationToken cancellationToken)
    {
        WatchSnapshotToken? snapshotToken;
        try
        {
            if (_monitor.TryCaptureSynchronizedSnapshot(out snapshotToken))
            {
                ProcessCapturedRefresh(request, snapshotToken, cancellationToken);
                return;
            }
        }
        catch (ObjectDisposedException)
        {
        }

        PublishNotReady(
            request,
            WatchRootPreviewOutcome.Suppressed,
            WatchRootPreviewProblemKind.StaleSnapshot,
            "The watch root is not synchronized for this refresh.");
    }

    private void ProcessCapturedRefresh(
        RefreshRequest request,
        WatchSnapshotToken snapshotToken,
        CancellationToken cancellationToken)
    {
        if (!ValidateRequestScope(request, snapshotToken))
        {
            return;
        }

        if (!TryCreatePlan(
                request,
                snapshotToken,
                cancellationToken,
                out FileScanResult? scan,
                out OrganizationPlan? plan))
        {
            return;
        }

        PhysicalEntryIdentity? acknowledgedIdentity;
        lock (_gate)
        {
            acknowledgedIdentity = _acknowledgedRootIdentity;
        }

        if (acknowledgedIdentity is null
            || !acknowledgedIdentity.Equals(scan.RootPhysicalIdentity))
        {
            FailSynchronized(
                request,
                snapshotToken,
                WatchRootPreviewProblemKind.SourceRootIdentityChanged,
                "The source-root physical identity no longer matches the accepted baseline.",
                WatchDiagnosticKind.DirectoryScopeChanged);
            return;
        }

        if (request.Candidate is not null && !ValidateCandidate(scan, request.Candidate))
        {
            FailSynchronized(
                request,
                snapshotToken,
                FindFile(scan, request.Candidate.Path) is null
                    ? WatchRootPreviewProblemKind.CandidateMissing
                    : WatchRootPreviewProblemKind.CandidateChanged,
                "The stable candidate no longer matches the full-root snapshot.",
                WatchDiagnosticKind.Unstable);
            return;
        }

        PublishReadyIfCurrent(request, snapshotToken, plan);
    }

    private bool TryCreatePlan(
        RefreshRequest request,
        WatchSnapshotToken? snapshotToken,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out FileScanResult? scan,
        [NotNullWhen(true)] out OrganizationPlan? plan)
    {
        scan = null;
        plan = null;
        try
        {
            scan = _scanner.Scan(
                _watchRoot.RootPath,
                _watchRoot.IncludeSubdirectories,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            FailRequest(
                request,
                snapshotToken,
                WatchRootPreviewProblemKind.SourceScanFailed,
                "The read-only source scan failed.",
                WatchDiagnosticKind.InputOutputFailure);
            return false;
        }

        if (scan.CanonicalRoot.Semantics != _pathPolicy.Semantics
            || !_pathPolicy.AreEquivalent(scan.CanonicalRoot, _watchRoot.RootPath)
            || !scan.IsComplete)
        {
            FailRequest(
                request,
                snapshotToken,
                WatchRootPreviewProblemKind.IncompleteSourceScan,
                "The full source scan did not account for the configured root.",
                WatchDiagnosticKind.IncompleteBaseline);
            return false;
        }

        if (scan.RootPhysicalIdentity?.EntryKind != PhysicalEntryKind.Directory)
        {
            FailRequest(
                request,
                snapshotToken,
                WatchRootPreviewProblemKind.MissingSourceRootIdentity,
                "The full source scan did not establish strong directory identity for the root.",
                WatchDiagnosticKind.UnsupportedCapability);
            return false;
        }

        OrganizationPlanPreparation preparation;
        try
        {
            preparation = _planner.Prepare(scan, _rules, _pathPolicy);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            FailRequest(
                request,
                snapshotToken,
                WatchRootPreviewProblemKind.PlanningFailed,
                "The immutable plan preparation failed.",
                WatchDiagnosticKind.InputOutputFailure);
            return false;
        }

        try
        {
            plan = OrganizationPlanner.Complete(
                preparation,
                _destinationInspector.Inspect(preparation, cancellationToken));
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            FailRequest(
                request,
                snapshotToken,
                WatchRootPreviewProblemKind.DestinationInspectionFailed,
                "The read-only destination inspection failed.",
                WatchDiagnosticKind.InputOutputFailure);
            return false;
        }
    }

    private bool ValidateRequestScope(
        RefreshRequest request,
        WatchSnapshotToken snapshotToken)
    {
        CanonicalPath path = request.Candidate?.Path ?? request.AffectedPath!;
        bool valid = (request.Candidate is null
                || request.Candidate.WatchRootId == WatchRootId)
            && path.Semantics == _pathPolicy.Semantics
            && _pathPolicy.Relate(_watchRoot.RootPath, path) == PathRelationship.Descendant
            && (_watchRoot.IncludeSubdirectories
                || _pathPolicy.GetParent(path) is { } parent
                && _pathPolicy.AreEquivalent(parent, _watchRoot.RootPath));
        if (valid)
        {
            return true;
        }

        FailSynchronized(
            request,
            snapshotToken,
            WatchRootPreviewProblemKind.InvalidCandidate,
            "The refresh request does not belong to the configured watch-root scope.",
            WatchDiagnosticKind.OutsideRoot);
        return false;
    }

    private bool ValidateCandidate(FileScanResult scan, StableFileCandidate candidate)
    {
        FileEntry? file = FindFile(scan, candidate.Path);
        return file is not null
            && candidate.Identity.Equals(file.PhysicalIdentity)
            && candidate.Metadata.Length == file.SizeInBytes
            && candidate.Metadata.CreatedUtc == file.CreatedUtc
            && candidate.Metadata.ModifiedUtc == file.ModifiedUtc;
    }

    private FileEntry? FindFile(FileScanResult scan, CanonicalPath path) =>
        FindUniqueFile(scan.Files, path);

    private FileEntry? FindUniqueFile(
        IReadOnlyList<FileEntry> files,
        CanonicalPath path)
    {
        FileEntry? match = null;
        foreach (FileEntry file in files)
        {
            if (!_pathPolicy.AreEquivalent(file.Path, path))
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = file;
        }

        return match;
    }

    private void FailRequest(
        RefreshRequest request,
        WatchSnapshotToken? snapshotToken,
        WatchRootPreviewProblemKind problemKind,
        string description,
        WatchDiagnosticKind diagnosticKind)
    {
        if (snapshotToken is null)
        {
            bool portFailure = problemKind is WatchRootPreviewProblemKind.SourceScanFailed
                or WatchRootPreviewProblemKind.PlanningFailed
                or WatchRootPreviewProblemKind.DestinationInspectionFailed;
            PublishNotReady(
                request,
                portFailure
                    ? WatchRootPreviewOutcome.Failed
                    : WatchRootPreviewOutcome.RescanRequired,
                problemKind,
                description);
            return;
        }

        FailSynchronized(
            request,
            snapshotToken,
            problemKind,
            description,
            diagnosticKind);
    }

    private void FailSynchronized(
        RefreshRequest request,
        WatchSnapshotToken snapshotToken,
        WatchRootPreviewProblemKind problemKind,
        string description,
        WatchDiagnosticKind diagnosticKind)
    {
        bool invalidated = _monitor.RequireRescan(
            snapshotToken,
            new WatchDiagnostic(diagnosticKind, description));
        PublishNotReady(
            request,
            invalidated
                ? WatchRootPreviewOutcome.RescanRequired
                : WatchRootPreviewOutcome.Suppressed,
            invalidated ? problemKind : WatchRootPreviewProblemKind.StaleSnapshot,
            invalidated
                ? description
                : "The synchronized snapshot became stale before failure classification.",
            snapshotToken);
    }

    private void PublishReadyIfCurrent(
        RefreshRequest request,
        WatchSnapshotToken snapshotToken,
        OrganizationPlan plan)
    {
        if (!_monitor.IsSnapshotCurrent(snapshotToken))
        {
            PublishNotReady(
                request,
                WatchRootPreviewOutcome.Suppressed,
                WatchRootPreviewProblemKind.StaleSnapshot,
                "A filesystem event invalidated the preview before publication.",
                snapshotToken);
            return;
        }

        Publish(
            WatchRootPreviewResult.Ready(
                WatchRootId,
                request.Trigger,
                plan,
                snapshotToken,
                request.Candidate,
                request.AffectedPath));
    }

    private void PublishNotReady(
        RefreshRequest request,
        WatchRootPreviewOutcome outcome,
        WatchRootPreviewProblemKind problemKind,
        string description,
        WatchSnapshotToken? snapshotToken = null) =>
        Publish(
            WatchRootPreviewResult.NotReady(
                WatchRootId,
                request.Trigger,
                outcome,
                new WatchRootPreviewProblem(problemKind, description),
                snapshotToken,
                request.Candidate,
                request.AffectedPath));

    private void Publish(WatchRootPreviewResult result)
    {
        EventHandler<WatchRootPreviewResult>? handlers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            handlers = PreviewProduced;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<WatchRootPreviewResult> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, result);
            }
            catch (Exception)
            {
                // One presentation subscriber cannot break later preview consumers.
            }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static TaskCompletionSource CompletedSignal()
    {
        TaskCompletionSource signal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private sealed record RefreshRequest(
        WatchRootPreviewTrigger Trigger,
        StableFileCandidate? Candidate,
        CanonicalPath? AffectedPath)
    {
        public static RefreshRequest ForBaseline() =>
            new(WatchRootPreviewTrigger.Baseline, null, null);

        public static RefreshRequest ForCandidate(StableFileCandidate candidate) =>
            new(WatchRootPreviewTrigger.StableCandidate, candidate, null);

        public static RefreshRequest ForAffectedPath(CanonicalPath affectedPath) =>
            new(WatchRootPreviewTrigger.RefreshRequested, null, affectedPath);
    }
}
