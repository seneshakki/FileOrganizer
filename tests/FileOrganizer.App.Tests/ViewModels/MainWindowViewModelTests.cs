using System.Globalization;
using FileOrganizer.App.Services;
using FileOrganizer.App.Startup;
using FileOrganizer.App.ViewModels;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Application.Watching;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.App.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    private static readonly Guid FirstRootId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondRootId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task MissingStartupArgumentPerformsNoStorageOrRuntimeWork()
    {
        ScriptedTextStore store = new(ConfigurationTextReadResult.NotFound());
        RecordingRuntimeFactory factory = new();
        MainWindowViewModel viewModel = new(
            ConfigurationPathArguments.Parse([]),
            new OrganizationConfigurationService(store),
            factory,
            new QueuedDispatcher());

        await viewModel.InitializeAsync();

        Assert.Equal(ConfigurationLoadState.AwaitingPath, viewModel.LoadState);
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, store.WriteCount);
        Assert.Equal(0, factory.CreateCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task MissingConfigurationDoesNotCreateRuntimeOrWriteStorage()
    {
        ScriptedTextStore store = new(ConfigurationTextReadResult.NotFound());
        RecordingRuntimeFactory factory = new();
        MainWindowViewModel viewModel = CreateViewModel(store, factory, new QueuedDispatcher());

        await viewModel.InitializeAsync();

        Assert.Equal(ConfigurationLoadState.Missing, viewModel.LoadState);
        Assert.True(viewModel.HasProblems);
        Assert.Equal(0, store.WriteCount);
        Assert.Equal(0, factory.CreateCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task MalformedConfigurationSurfacesValidationWithoutStartingRuntime()
    {
        ScriptedTextStore store = new(ConfigurationTextReadResult.Found("{ nope"));
        RecordingRuntimeFactory factory = new();
        MainWindowViewModel viewModel = CreateViewModel(store, factory, new QueuedDispatcher());

        await viewModel.InitializeAsync();

        Assert.Equal(ConfigurationLoadState.Invalid, viewModel.LoadState);
        Assert.Contains(viewModel.Problems, static problem => problem.Contains("MalformedJson", StringComparison.Ordinal));
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, store.WriteCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task StorageFailureRemainsStructuredAndDoesNotStartRuntime()
    {
        ScriptedTextStore store = new(
            ConfigurationTextReadResult.Failed(
                [
                    new ConfigurationStorageProblem(
                        ConfigurationStorageProblemKind.Inaccessible,
                        "Access denied."),
                ]));
        RecordingRuntimeFactory factory = new();
        MainWindowViewModel viewModel = CreateViewModel(store, factory, new QueuedDispatcher());

        await viewModel.InitializeAsync();

        Assert.Equal(ConfigurationLoadState.StorageFailure, viewModel.LoadState);
        Assert.Contains("Inaccessible", Assert.Single(viewModel.Problems), StringComparison.Ordinal);
        Assert.Equal(0, factory.CreateCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task RuntimeSnapshotIsProjectedInConfiguredOrderOnlyAfterUiDispatch()
    {
        ScriptedTextStore store = new(ConfigurationTextReadResult.Found(ConfigurationJson()));
        QueuedDispatcher dispatcher = new();
        RecordingRuntimeFactory factory = new();
        MainWindowViewModel viewModel = CreateViewModel(store, factory, dispatcher);
        await viewModel.InitializeAsync();
        FakeRuntime runtime = Assert.IsType<FakeRuntime>(factory.Runtime);

        WatchPreviewRuntimeSnapshot updated = SnapshotWithHistoricalPreview(factory.Configuration!);
        runtime.Publish(updated, [FirstRootId]);

        Assert.Equal(WatchRootState.RequiresInitialScan.ToString(), viewModel.Roots[0].State);
        Assert.Equal(1, dispatcher.PendingCount);

        dispatcher.RunAll();

        Assert.Equal([FirstRootId, SecondRootId], viewModel.Roots.Select(static root => root.Id));
        WatchRootViewModel first = viewModel.Roots[0];
        Assert.Equal(WatchRootState.Synchronized.ToString(), first.State);
        Assert.Equal(WatchRootPreviewOutcome.Ready.ToString(), first.PreviewOutcome);
        Assert.Equal(1, first.TotalCount);
        Assert.Equal(1, first.SkippedCount);
        PlanItemViewModel item = Assert.Single(first.PlanItems);
        Assert.Equal("report.txt", item.SourceName);
        Assert.Equal(PlanItemOutcome.Skipped.ToString(), item.Outcome);
        Assert.Contains("No enabled", item.Reasons, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BufferOverflow", first.LatestDiagnostic, StringComparison.Ordinal);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task OlderNotificationQueuedDuringStartCannotReplaceNewerSnapshot()
    {
        ScriptedTextStore store = new(ConfigurationTextReadResult.Found(ConfigurationJson()));
        QueuedDispatcher dispatcher = new();
        RecordingRuntimeFactory factory = new()
        {
            RuntimeSetup = static (configuration, runtime) => runtime.PublishDuringStart(
                SnapshotWithFirstState(configuration, WatchRootState.RescanRequired, 1),
                SnapshotWithFirstState(configuration, WatchRootState.Synchronized, 2),
                [FirstRootId]),
        };
        MainWindowViewModel viewModel = CreateViewModel(store, factory, dispatcher);

        await viewModel.InitializeAsync();

        Assert.Equal(WatchRootState.Synchronized.ToString(), viewModel.Roots[0].State);
        Assert.Equal(1, dispatcher.PendingCount);
        dispatcher.RunAll();
        Assert.Equal(WatchRootState.Synchronized.ToString(), viewModel.Roots[0].State);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task RootCommandsUseRuntimeBoundaryAndDisposalIsJoined()
    {
        ScriptedTextStore store = new(ConfigurationTextReadResult.Found(ConfigurationJson()));
        RecordingRuntimeFactory factory = new();
        QueuedDispatcher dispatcher = new();
        MainWindowViewModel viewModel = CreateViewModel(store, factory, dispatcher);
        await viewModel.InitializeAsync();
        FakeRuntime runtime = Assert.IsType<FakeRuntime>(factory.Runtime);

        WatchRootViewModel root = viewModel.Roots[0];
        Assert.True(root.PauseCommand.CanExecute(null));
        Assert.True(root.RetryCommand.CanExecute(null));
        root.RetryCommand.Execute(null);
        root.PauseCommand.Execute(null);

        runtime.Publish(
            SnapshotWithFirstState(factory.Configuration!, WatchRootState.Paused),
            [FirstRootId]);
        dispatcher.RunAll();
        root = viewModel.Roots[0];
        Assert.True(root.ResumeCommand.CanExecute(null));
        root.ResumeCommand.Execute(null);

        Assert.Equal(FirstRootId, Assert.Single(runtime.Paused));
        Assert.Equal(FirstRootId, Assert.Single(runtime.Resumed));
        Assert.Equal(FirstRootId, Assert.Single(runtime.BaselineRequests));

        runtime.BlockDisposal();
        Task disposal = viewModel.DisposeAsync().AsTask();
        Assert.True(runtime.DisposalStarted);
        Assert.False(disposal.IsCompleted);
        runtime.ReleaseDisposal();
        await disposal;

        Assert.True(runtime.Disposed);
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public async Task ShutdownObservesNonCancellationInitializationCleanupFailureWithoutFaultingInitializer()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        using ManualResetEventSlim startEntered = new();
        using ManualResetEventSlim releaseStart = new();
        ScriptedTextStore store = new(ConfigurationTextReadResult.Found(ConfigurationJson()));
        RecordingRuntimeFactory factory = new()
        {
            RuntimeSetup = (_, runtime) =>
            {
                runtime.StartAction = () =>
                {
                    startEntered.Set();
                    releaseStart.Wait(testCancellation);
                    throw new InvalidOperationException("Start interrupted during shutdown.");
                };
                runtime.DisposeFailure = new IOException("Cleanup failed.");
            },
        };
        MainWindowViewModel viewModel = CreateViewModel(store, factory, new QueuedDispatcher());
        Task initialization = Task.Run(viewModel.InitializeAsync, testCancellation);
        Assert.True(startEntered.Wait(TimeSpan.FromSeconds(5), testCancellation));

        Task disposal = viewModel.DisposeAsync().AsTask();
        releaseStart.Set();

        await initialization;
        IOException exception = await Assert.ThrowsAsync<IOException>(() => disposal);
        Assert.Equal("Cleanup failed.", exception.Message);
    }

    [Fact]
    public async Task DisposalPreservesInitializationFailureWhenNoCleanupFailureWasRecorded()
    {
        ScriptedTextStore store = new(ConfigurationTextReadResult.Found(ConfigurationJson()));
        RecordingRuntimeFactory factory = new();
        MainWindowViewModel viewModel = CreateViewModel(store, factory, new QueuedDispatcher());
        viewModel.PropertyChanged += static (_, _) =>
            throw new InvalidOperationException("Presentation subscriber failed.");

        InvalidOperationException initializationFailure =
            await Assert.ThrowsAsync<InvalidOperationException>(viewModel.InitializeAsync);
        InvalidOperationException disposalFailure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => viewModel.DisposeAsync().AsTask());

        Assert.Same(initializationFailure, disposalFailure);
    }

    private static MainWindowViewModel CreateViewModel(
        ScriptedTextStore store,
        RecordingRuntimeFactory factory,
        QueuedDispatcher dispatcher)
    {
        string path = Path.Combine(Path.GetTempPath(), "file-organizer-tests", "config.json");
        return new MainWindowViewModel(
            ConfigurationPathArguments.Parse(["--config", path]),
            new OrganizationConfigurationService(store),
            factory,
            dispatcher);
    }

    private static WatchPreviewRuntimeSnapshot SnapshotWithHistoricalPreview(
        OrganizationConfiguration configuration)
    {
        PathPolicy policy = Policy(configuration.PathSemantics);
        CanonicalPath root = Canonical(policy, @"C:\WatchA");
        CanonicalPath filePath = Canonical(policy, @"C:\WatchA\report.txt");
        FileScanResult scan = new(
            root,
            [
                new FileEntry(
                    filePath,
                    42,
                    null,
                    DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture)),
            ],
            []);
        OrganizationPlanPreparation preparation = new OrganizationPlanner(TimeProvider.System)
            .Prepare(scan, configuration.RuleSet, policy);
        OrganizationPlan plan = OrganizationPlanner.Complete(
            preparation,
            new DestinationStateSnapshot(configuration.PathSemantics, []));
        WatchRootPreviewResult preview = WatchRootPreviewResult.CreateReady(
            FirstRootId,
            WatchRootPreviewTrigger.Baseline,
            plan,
            new WatchSnapshotToken(FirstRootId, 3, 2));

        return new WatchPreviewRuntimeSnapshot(
            7,
            [
                new WatchPreviewRootSnapshot(
                    FirstRootId,
                    root,
                    true,
                    true,
                    WatchRootState.Synchronized,
                    5,
                    new WatchDiagnostic(
                        WatchDiagnosticKind.BufferOverflow,
                        "A prior event generation overflowed."),
                    preview),
                new WatchPreviewRootSnapshot(
                    SecondRootId,
                    Canonical(policy, @"D:\WatchB"),
                    false,
                    false,
                    WatchRootState.Paused,
                    1,
                    null,
                    null),
            ]);
    }

    private static PathPolicy Policy(PathSemantics semantics) => new(
        semantics.Syntax,
        semantics.CaseSensitivity,
        semantics.UnicodeNormalization);

    private static WatchPreviewRuntimeSnapshot SnapshotWithFirstState(
        OrganizationConfiguration configuration,
        WatchRootState state,
        long revision = 1) =>
        new(
            revision,
            configuration.WatchRoots.Select(
                root => new WatchPreviewRootSnapshot(
                    root.Id,
                    root.RootPath,
                    root.IsEnabled,
                    root.IncludeSubdirectories,
                    root.Id == FirstRootId ? state : WatchRootState.Paused,
                    1,
                    null,
                    null)));

    private static CanonicalPath Canonical(PathPolicy policy, string path)
    {
        PathResolution result = policy.CanonicalizeAbsolute(path);
        Assert.True(result.Succeeded, result.Problem?.Description);
        return result.Path!;
    }

    private static string ConfigurationJson() => $$"""
        {
          "schemaVersion": 1,
          "pathPolicy": {
            "syntax": "windows",
            "caseSensitivity": "insensitive",
            "unicodeNormalization": "formC"
          },
          "watchRoots": [
            {
              "id": "{{FirstRootId:D}}",
              "rootPath": "C:\\WatchA",
              "enabled": true,
              "includeSubdirectories": true
            },
            {
              "id": "{{SecondRootId:D}}",
              "rootPath": "D:\\WatchB",
              "enabled": false,
              "includeSubdirectories": false
            }
          ],
          "rules": []
        }
        """;

    private sealed class ScriptedTextStore(ConfigurationTextReadResult readResult)
        : IConfigurationTextStore
    {
        public int ReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public ValueTask<ConfigurationTextReadResult> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(readResult);
        }

        public ValueTask<ConfigurationTextWriteResult> WriteAsync(
            string path,
            string content,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            throw new InvalidOperationException("The read-only dashboard must never write configuration.");
        }
    }

    private sealed class RecordingRuntimeFactory : IWatchPreviewRuntimeFactory
    {
        public int CreateCount { get; private set; }

        public OrganizationConfiguration? Configuration { get; private set; }

        public IWatchPreviewRuntime? Runtime { get; private set; }

        public Action<OrganizationConfiguration, FakeRuntime>? RuntimeSetup { get; init; }

        public IWatchPreviewRuntime Create(OrganizationConfiguration configuration)
        {
            CreateCount++;
            Configuration = configuration;
            var runtime = new FakeRuntime(InitialSnapshot(configuration));
            RuntimeSetup?.Invoke(configuration, runtime);
            Runtime = runtime;
            return Runtime;
        }

        private static WatchPreviewRuntimeSnapshot InitialSnapshot(
            OrganizationConfiguration configuration) =>
            new(
                0,
                configuration.WatchRoots.Select(
                    static root => new WatchPreviewRootSnapshot(
                        root.Id,
                        root.RootPath,
                        root.IsEnabled,
                        root.IncludeSubdirectories,
                        root.IsEnabled
                            ? WatchRootState.RequiresInitialScan
                            : WatchRootState.Paused,
                        0,
                        null,
                        null)));
    }

    private sealed class FakeRuntime(WatchPreviewRuntimeSnapshot snapshot) : IWatchPreviewRuntime
    {
        private TaskCompletionSource? _disposeRelease;
        private WatchPreviewRuntimeSnapshot? _startNotification;
        private WatchPreviewRuntimeSnapshot? _postStartSnapshot;
        private Guid[]? _startChangedRootIds;

        public event EventHandler<WatchPreviewRuntimeNotification>? SnapshotChanged;

        public WatchPreviewRuntimeSnapshot Snapshot { get; private set; } = snapshot;

        public List<Guid> Paused { get; } = [];

        public List<Guid> Resumed { get; } = [];

        public List<Guid> BaselineRequests { get; } = [];

        public bool Disposed { get; private set; }

        public bool DisposalStarted { get; private set; }

        public Action? StartAction { get; set; }

        public Exception? DisposeFailure { get; set; }

        public void Start()
        {
            StartAction?.Invoke();
            if (_startNotification is not null
                && _postStartSnapshot is not null
                && _startChangedRootIds is not null)
            {
                Snapshot = _startNotification;
                SnapshotChanged?.Invoke(
                    this,
                    new WatchPreviewRuntimeNotification(
                        _startNotification,
                        _startChangedRootIds));
                Snapshot = _postStartSnapshot;
            }
        }

        public bool PauseRoot(Guid watchRootId)
        {
            Paused.Add(watchRootId);
            return true;
        }

        public bool ResumeRoot(Guid watchRootId)
        {
            Resumed.Add(watchRootId);
            return true;
        }

        public bool RequestBaselineScan(Guid watchRootId)
        {
            BaselineRequests.Add(watchRootId);
            return true;
        }

        public ValueTask DrainAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => new(DisposeCoreAsync());

        public void BlockDisposal() => _disposeRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseDisposal() => _disposeRelease?.TrySetResult();

        public void PublishDuringStart(
            WatchPreviewRuntimeSnapshot notification,
            WatchPreviewRuntimeSnapshot postStartSnapshot,
            IEnumerable<Guid> changedRootIds)
        {
            _startNotification = notification;
            _postStartSnapshot = postStartSnapshot;
            _startChangedRootIds = [.. changedRootIds];
        }

        private async Task DisposeCoreAsync()
        {
            DisposalStarted = true;
            if (_disposeRelease is not null)
            {
                await _disposeRelease.Task;
            }

            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }

            Disposed = true;
            SnapshotChanged = null;
        }

        public void Publish(WatchPreviewRuntimeSnapshot updated, IEnumerable<Guid> changedRootIds)
        {
            Snapshot = updated;
            SnapshotChanged?.Invoke(
                this,
                new WatchPreviewRuntimeNotification(updated, changedRootIds));
        }
    }

    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _actions = new();

        public int PendingCount => _actions.Count;

        public void Post(Action action) => _actions.Enqueue(action);

        public void RunAll()
        {
            while (_actions.TryDequeue(out Action? action))
            {
                action();
            }
        }
    }
}
