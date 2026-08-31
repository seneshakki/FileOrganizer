using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using FileOrganizer.App.Services;
using FileOrganizer.App.Startup;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Application.Watching;

namespace FileOrganizer.App.ViewModels;

/// <summary>Desktop shell for validated configuration editing and read-only planning previews.</summary>
public sealed class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IWatchPreviewRuntimeFactory _runtimeFactory;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<IWatchPreviewRuntime> _cleanupPending = [];
    private ReadOnlyCollection<WatchRootViewModel> _roots =
        Array.AsReadOnly(Array.Empty<WatchRootViewModel>());
    private IWatchPreviewRuntime? _runtime;
    private Task? _disposal;
    private long _lastAppliedRevision = -1;
    private bool _disposed;

    public MainWindowViewModel(
        ConfigurationPathArguments arguments,
        OrganizationConfigurationService configurationService,
        IWatchPreviewRuntimeFactory runtimeFactory,
        IUiDispatcher dispatcher,
        IConfigurationPathPicker pathPicker)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(configurationService);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(pathPicker);

        _runtimeFactory = runtimeFactory;
        _dispatcher = dispatcher;
        Editor = new ConfigurationEditorViewModel(
            arguments,
            configurationService,
            pathPicker,
            ActivateConfigurationAsync);
        Editor.PropertyChanged += OnEditorPropertyChanged;
    }

    public string Title { get; } = "FileOrganizer";

    public string Subtitle { get; } = "Validated configuration and read-only planning workspace";

    public ConfigurationEditorViewModel Editor { get; }

    public string ConfigurationPath => Editor.ConfigurationPath;

    public ConfigurationLoadState LoadState => Editor.LoadState;

    public string Status => Editor.Status;

    public IReadOnlyList<string> Problems => Editor.Problems;

    public bool HasProblems => Editor.HasProblems;

    public IReadOnlyList<WatchRootViewModel> Roots => _roots;

    public bool HasRoots => _roots.Count != 0;

    public bool ShowEmptyConfiguration =>
        LoadState == ConfigurationLoadState.Loaded && !HasRoots;

    public Task InitializeAsync() => Editor.InitializeAsync();

    public ValueTask DisposeAsync()
    {
        Task disposal;
        lock (_gate)
        {
            _disposal ??= DisposeCoreAsync();
            disposal = _disposal;
        }

        return new ValueTask(disposal);
    }

    private async Task ActivateConfigurationAsync(
        OrganizationConfiguration configuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IWatchPreviewRuntime? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _runtime;
            _runtime = null;
            _lastAppliedRevision = -1;
        }

        if (previous is not null)
        {
            previous.SnapshotChanged -= OnSnapshotChanged;
        }

        ClearRoots();
        if (previous is not null)
        {
            try
            {
                await previous.DisposeAsync();
            }
            catch
            {
                TrackCleanupFailure(previous);
                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        IWatchPreviewRuntime runtime = _runtimeFactory.Create(configuration)
            ?? throw new InvalidOperationException("The preview runtime factory returned null.");
        runtime.SnapshotChanged += OnSnapshotChanged;
        bool disposeImmediately;
        lock (_gate)
        {
            disposeImmediately = _disposed;
            if (disposeImmediately)
            {
                runtime.SnapshotChanged -= OnSnapshotChanged;
            }
            else
            {
                _runtime = runtime;
            }
        }

        if (disposeImmediately)
        {
            await runtime.DisposeAsync();
        }

        ObjectDisposedException.ThrowIf(disposeImmediately, this);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            runtime.Start();
            ApplySnapshot(runtime, runtime.Snapshot);
        }
        catch (Exception startFailure)
        {
            runtime.SnapshotChanged -= OnSnapshotChanged;
            lock (_gate)
            {
                if (ReferenceEquals(_runtime, runtime))
                {
                    _runtime = null;
                }
            }

            try
            {
                await runtime.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                TrackCleanupFailure(runtime);
                throw new AggregateException(startFailure, cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(startFailure).Throw();
        }
    }

    private void OnSnapshotChanged(object? sender, WatchPreviewRuntimeNotification notification)
    {
        if (sender is not IWatchPreviewRuntime runtime)
        {
            return;
        }

        _dispatcher.Post(
            () => ApplySnapshot(runtime, notification.Snapshot));
    }

    private void TrackCleanupFailure(IWatchPreviewRuntime runtime)
    {
        lock (_gate)
        {
            if (!_cleanupPending.Contains(runtime, ReferenceEqualityComparer.Instance))
            {
                _cleanupPending.Add(runtime);
            }
        }
    }

    private void ApplySnapshot(
        IWatchPreviewRuntime runtime,
        WatchPreviewRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(snapshot);
        ReadOnlyCollection<WatchRootViewModel> roots = Array.AsReadOnly(
            snapshot.Roots.Select(
                root => new WatchRootViewModel(
                    root,
                    PauseRoot,
                    ResumeRoot,
                    RetryBaseline))
                .ToArray());
        lock (_gate)
        {
            if (_disposed
                || !ReferenceEquals(runtime, _runtime)
                || snapshot.Revision <= _lastAppliedRevision)
            {
                return;
            }

            _lastAppliedRevision = snapshot.Revision;
            _roots = roots;
        }

        RaiseRootsChanged();
    }

    private void ClearRoots()
    {
        bool changed;
        lock (_gate)
        {
            changed = _roots.Count != 0;
            _roots = Array.AsReadOnly(Array.Empty<WatchRootViewModel>());
        }

        if (changed)
        {
            RaiseRootsChanged();
        }
    }

    private void RaiseRootsChanged()
    {
        RaisePropertyChanged(nameof(Roots));
        RaisePropertyChanged(nameof(HasRoots));
        RaisePropertyChanged(nameof(ShowEmptyConfiguration));
    }

    private bool PauseRoot(Guid watchRootId) => InvokeRuntimeCommand(
        watchRootId,
        static (runtime, id) => runtime.PauseRoot(id));

    private bool ResumeRoot(Guid watchRootId) => InvokeRuntimeCommand(
        watchRootId,
        static (runtime, id) => runtime.ResumeRoot(id));

    private bool RetryBaseline(Guid watchRootId) => InvokeRuntimeCommand(
        watchRootId,
        static (runtime, id) => runtime.RequestBaselineScan(id));

    private bool InvokeRuntimeCommand(
        Guid watchRootId,
        Func<IWatchPreviewRuntime, Guid, bool> command)
    {
        IWatchPreviewRuntime? runtime;
        lock (_gate)
        {
            runtime = _disposed ? null : _runtime;
        }

        return runtime is not null && command(runtime, watchRootId);
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        switch (eventArgs.PropertyName)
        {
            case nameof(ConfigurationEditorViewModel.ConfigurationPath):
                RaisePropertyChanged(nameof(ConfigurationPath));
                break;
            case nameof(ConfigurationEditorViewModel.LoadState):
                RaisePropertyChanged(nameof(LoadState));
                RaisePropertyChanged(nameof(ShowEmptyConfiguration));
                break;
            case nameof(ConfigurationEditorViewModel.Status):
                RaisePropertyChanged(nameof(Status));
                break;
            case nameof(ConfigurationEditorViewModel.Problems):
                RaisePropertyChanged(nameof(Problems));
                break;
            case nameof(ConfigurationEditorViewModel.HasProblems):
                RaisePropertyChanged(nameof(HasProblems));
                break;
        }
    }

    private async Task DisposeCoreAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Exception? failure = null;
        try
        {
            await Editor.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            Editor.PropertyChanged -= OnEditorPropertyChanged;
        }

        IWatchPreviewRuntime? runtime;
        IWatchPreviewRuntime[] cleanupPending;
        lock (_gate)
        {
            runtime = _runtime;
            _runtime = null;
            cleanupPending = [.. _cleanupPending];
            _cleanupPending.Clear();
        }

        IWatchPreviewRuntime[] runtimes = runtime is null
            ? cleanupPending
            : [runtime, .. cleanupPending.Where(candidate => !ReferenceEquals(candidate, runtime))];
        foreach (IWatchPreviewRuntime candidate in runtimes)
        {
            candidate.SnapshotChanged -= OnSnapshotChanged;
            try
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
