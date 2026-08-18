using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using FileOrganizer.App.Services;
using FileOrganizer.App.Startup;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Application.Watching;

namespace FileOrganizer.App.ViewModels;

/// <summary>Read-only desktop projection of configuration and watched-root planning previews.</summary>
public sealed class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ConfigurationPathArguments _arguments;
    private readonly OrganizationConfigurationService _configurationService;
    private readonly IWatchPreviewRuntimeFactory _runtimeFactory;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ReadOnlyCollection<string> _emptyProblems = Array.AsReadOnly(Array.Empty<string>());

    private ReadOnlyCollection<string> _problems;
    private ReadOnlyCollection<WatchRootViewModel> _roots =
        Array.AsReadOnly(Array.Empty<WatchRootViewModel>());
    private ConfigurationLoadState _loadState = ConfigurationLoadState.AwaitingPath;
    private string _status = "A configuration path is required.";
    private IWatchPreviewRuntime? _runtime;
    private Task? _initialization;
    private Task? _disposal;
    private Exception? _initializationFailure;
    private long _lastAppliedRevision = -1;
    private bool _disposed;

    public MainWindowViewModel(
        ConfigurationPathArguments arguments,
        OrganizationConfigurationService configurationService,
        IWatchPreviewRuntimeFactory runtimeFactory,
        IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(configurationService);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _arguments = arguments;
        _configurationService = configurationService;
        _runtimeFactory = runtimeFactory;
        _dispatcher = dispatcher;
        _problems = _emptyProblems;
        if (arguments.Problem is not null)
        {
            _problems = Array.AsReadOnly(new[] { arguments.Problem });
        }
    }

    public string Title { get; } = "FileOrganizer";

    public string Subtitle { get; } = "Read-only watched-folder planning dashboard";

    public string ConfigurationPath => _arguments.ConfigurationPath ?? "No configuration selected";

    public ConfigurationLoadState LoadState => _loadState;

    public string Status => _status;

    public IReadOnlyList<string> Problems => _problems;

    public bool HasProblems => _problems.Count != 0;

    public IReadOnlyList<WatchRootViewModel> Roots => _roots;

    public bool HasRoots => _roots.Count != 0;

    public bool ShowEmptyConfiguration => LoadState == ConfigurationLoadState.Loaded && !HasRoots;

    public Task InitializeAsync()
    {
        TaskCompletionSource? start = null;
        Task initialization;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialization is null)
            {
                start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _initialization = start.Task;
            }

            initialization = _initialization;
        }

        if (start is not null)
        {
            _ = CompleteInitializationAsync(start, _lifetime.Token);
        }

        return initialization;
    }

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

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (!_arguments.IsValid)
        {
            SetLoadState(
                ConfigurationLoadState.AwaitingPath,
                "Startup arguments are incomplete; no filesystem monitoring was started.");
            return;
        }

        SetLoadState(ConfigurationLoadState.Loading, "Loading and validating configuration…");
        try
        {
            ConfigurationLoadResult load = await _configurationService.LoadAsync(
                _arguments.ConfigurationPath!,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            switch (load.Status)
            {
                case ConfigurationLoadStatus.NotFound:
                    SetProblems(
                        ConfigurationLoadState.Missing,
                        "Configuration file was not found. Nothing was created.",
                        ["The caller-supplied configuration path does not exist."]);
                    return;
                case ConfigurationLoadStatus.Invalid:
                    SetProblems(
                        ConfigurationLoadState.Invalid,
                        "Configuration is invalid; no watcher was started.",
                        load.ConfigurationProblems.Select(static problem =>
                            $"{problem.Location} · {problem.Kind}: {problem.Description}"));
                    return;
                case ConfigurationLoadStatus.StorageFailure:
                    SetProblems(
                        ConfigurationLoadState.StorageFailure,
                        "Configuration could not be read; no watcher was started.",
                        load.StorageProblems.Select(static problem =>
                            $"{problem.Kind}: {problem.Description}"));
                    return;
                case ConfigurationLoadStatus.Loaded:
                    await StartRuntimeAsync(load.Configuration!, cancellationToken);
                    return;
                default:
                    throw new InvalidOperationException("Configuration load returned an unknown status.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                _initializationFailure ??= exception;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            SetProblems(
                ConfigurationLoadState.RuntimeFailure,
                "The read-only watch runtime could not be started.",
                [exception.Message]);
        }
    }

    private async Task CompleteInitializationAsync(
        TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            await InitializeCoreAsync(cancellationToken);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task StartRuntimeAsync(
        OrganizationConfiguration configuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IWatchPreviewRuntime runtime = _runtimeFactory.Create(configuration);
        runtime.SnapshotChanged += OnSnapshotChanged;
        bool disposeImmediately;
        lock (_gate)
        {
            disposeImmediately = _disposed;
            if (!disposeImmediately)
            {
                _runtime = runtime;
            }
        }

        if (disposeImmediately)
        {
            runtime.SnapshotChanged -= OnSnapshotChanged;
            await runtime.DisposeAsync();
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            runtime.Start();
            ApplySnapshot(runtime.Snapshot);
            SetLoadState(
                ConfigurationLoadState.Loaded,
                configuration.WatchRoots.Count == 0
                    ? "Configuration loaded. No watch roots are configured."
                    : $"Configuration loaded. Monitoring {configuration.WatchRoots.Count} configured root(s).",
                clearProblems: true);
        }
        catch
        {
            runtime.SnapshotChanged -= OnSnapshotChanged;
            lock (_gate)
            {
                if (ReferenceEquals(_runtime, runtime))
                {
                    _runtime = null;
                }
            }

            await runtime.DisposeAsync();
            throw;
        }
    }

    private void OnSnapshotChanged(object? sender, WatchPreviewRuntimeNotification notification)
    {
        _dispatcher.Post(
            () =>
            {
                lock (_gate)
                {
                    if (_disposed || !ReferenceEquals(sender, _runtime))
                    {
                        return;
                    }
                }

                ApplySnapshot(notification.Snapshot);
            });
    }

    private void ApplySnapshot(WatchPreviewRuntimeSnapshot snapshot)
    {
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
            if (_disposed || snapshot.Revision <= _lastAppliedRevision)
            {
                return;
            }

            _lastAppliedRevision = snapshot.Revision;
            _roots = roots;
        }

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

    private void SetLoadState(
        ConfigurationLoadState state,
        string status,
        bool clearProblems = false)
    {
        _loadState = state;
        _status = status;
        if (clearProblems)
        {
            _problems = _emptyProblems;
            RaisePropertyChanged(nameof(Problems));
            RaisePropertyChanged(nameof(HasProblems));
        }

        RaisePropertyChanged(nameof(LoadState));
        RaisePropertyChanged(nameof(Status));
        RaisePropertyChanged(nameof(ShowEmptyConfiguration));
    }

    private void SetProblems(
        ConfigurationLoadState state,
        string status,
        IEnumerable<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        string[] snapshot = [.. problems];
        _problems = Array.AsReadOnly(snapshot.Length == 0 ? ["An unspecified problem occurred."] : snapshot);
        SetLoadState(state, status);
        RaisePropertyChanged(nameof(Problems));
        RaisePropertyChanged(nameof(HasProblems));
    }

    private async Task DisposeCoreAsync()
    {
        Task? initialization;
        Exception? failure = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            initialization = _initialization;
        }

        if (initialization is not null)
        {
            try
            {
                await initialization.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        IWatchPreviewRuntime? runtime;
        lock (_gate)
        {
            failure = CombineFailures(failure, _initializationFailure);
            runtime = _runtime;
            _runtime = null;
        }

        try
        {
            if (runtime is not null)
            {
                runtime.SnapshotChanged -= OnSnapshotChanged;
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = CombineFailures(failure, exception);
        }
        finally
        {
            _lifetime.Dispose();
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static Exception? CombineFailures(Exception? first, Exception? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null || ReferenceEquals(first, second))
        {
            return first;
        }

        return new AggregateException(first, second);
    }
}
