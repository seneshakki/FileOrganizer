using System.Globalization;
using System.Text;
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
            new QueuedDispatcher(),
            new NullPathPicker(),
            new ScriptedUnsavedChangesPrompt());

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
        ScriptedTextStore store = new(Found("{ nope"));
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
        ScriptedTextStore store = new(Found(ConfigurationJson()));
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
        ScriptedTextStore store = new(Found(ConfigurationJson()));
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
        ScriptedTextStore store = new(Found(ConfigurationJson()));
        RecordingRuntimeFactory factory = new();
        QueuedDispatcher dispatcher = new();
        MainWindowViewModel viewModel = CreateViewModel(store, factory, dispatcher);
        await viewModel.InitializeAsync();
        FakeRuntime runtime = Assert.IsType<FakeRuntime>(factory.Runtime);

        WatchRootViewModel root = viewModel.Roots[0];
        Assert.True(root.PauseCommand.CanExecute(null));
        Assert.True(root.RetryCommand.CanExecute(null));
        Assert.False(root.ReconnectCommand.CanExecute(null));
        root.RetryCommand.Execute(null);
        root.PauseCommand.Execute(null);

        runtime.Publish(
            SnapshotWithFirstState(factory.Configuration!, WatchRootState.Paused),
            [FirstRootId]);
        dispatcher.RunAll();
        root = viewModel.Roots[0];
        Assert.True(root.ResumeCommand.CanExecute(null));
        root.ResumeCommand.Execute(null);

        runtime.Publish(
            SnapshotWithFirstState(factory.Configuration!, WatchRootState.Faulted, 2),
            [FirstRootId]);
        dispatcher.RunAll();
        root = viewModel.Roots[0];
        Assert.True(root.ReconnectCommand.CanExecute(null));
        Assert.False(root.RetryCommand.CanExecute(null));
        root.ReconnectCommand.Execute(null);

        Assert.Equal(FirstRootId, Assert.Single(runtime.Paused));
        Assert.Equal(FirstRootId, Assert.Single(runtime.Resumed));
        Assert.Equal(FirstRootId, Assert.Single(runtime.Reconnected));
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
    public async Task SavedConfigurationDisposesOldRuntimeAcceptsNewRevisionZeroAndIgnoresQueuedOldEvents()
    {
        ScriptedTextStore store = new(Found(ConfigurationJson()))
        {
            AllowWrites = true,
        };
        QueuedDispatcher dispatcher = new();
        FakeRuntime? oldRuntime = null;
        MainWindowViewModel? viewModel = null;
        RecordingRuntimeFactory factory = new()
        {
            RuntimeSetup = (configuration, runtime) =>
            {
                if (configuration.WatchRoots.Count == 1)
                {
                    runtime.StartAction = () =>
                    {
                        Assert.NotNull(oldRuntime);
                        Assert.False(oldRuntime.Disposed);
                        Assert.Equal([FirstRootId, SecondRootId], viewModel!.Roots.Select(static root => root.Id));
                        Assert.Equal(WatchRootState.Synchronized.ToString(), viewModel.Roots[0].State);
                    };
                }
            },
        };
        viewModel = CreateViewModel(store, factory, dispatcher);
        await viewModel.InitializeAsync();
        oldRuntime = Assert.IsType<FakeRuntime>(factory.Runtime);
        oldRuntime.Publish(
            SnapshotWithFirstState(factory.Configuration!, WatchRootState.Synchronized, 5),
            [FirstRootId]);
        dispatcher.RunAll();
        Assert.Equal(WatchRootState.Synchronized.ToString(), viewModel.Roots[0].State);

        oldRuntime.Publish(
            SnapshotWithFirstState(factory.Configuration!, WatchRootState.RescanRequired, 6),
            [FirstRootId]);
        viewModel.Editor.ConfigurationText = SingleRootConfigurationJson();
        await viewModel.Editor.SaveAsync(TestContext.Current.CancellationToken);

        Assert.True(oldRuntime.Disposed);
        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(SecondRootId, Assert.Single(viewModel.Roots).Id);
        Assert.Equal(WatchRootState.RequiresInitialScan.ToString(), viewModel.Roots[0].State);

        dispatcher.RunAll();

        Assert.Equal(SecondRootId, Assert.Single(viewModel.Roots).Id);
        Assert.Equal(WatchRootState.RequiresInitialScan.ToString(), viewModel.Roots[0].State);
        Assert.Equal(1, store.WriteCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task RuntimeCreationFailurePreservesActiveRuntimeAndVisibleSnapshot()
    {
        ScriptedTextStore store = new(Found(ConfigurationJson()))
        {
            AllowWrites = true,
        };
        QueuedDispatcher dispatcher = new();
        RecordingRuntimeFactory factory = new()
        {
            CreateFailure = static configuration => configuration.WatchRoots.Count == 1
                ? new InvalidOperationException("Creation failed.")
                : null,
        };
        MainWindowViewModel viewModel = CreateViewModel(store, factory, dispatcher);
        await viewModel.InitializeAsync();
        FakeRuntime oldRuntime = Assert.IsType<FakeRuntime>(factory.Runtime);
        OrganizationConfiguration oldConfiguration = factory.Configuration!;
        oldRuntime.Publish(
            SnapshotWithFirstState(oldConfiguration, WatchRootState.Synchronized, 5),
            [FirstRootId]);
        dispatcher.RunAll();

        viewModel.Editor.ConfigurationText = SingleRootConfigurationJson();
        await viewModel.Editor.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.RuntimeFailure, viewModel.LoadState);
        Assert.Equal(2, factory.CreateCount);
        Assert.False(oldRuntime.Disposed);
        Assert.Equal([FirstRootId, SecondRootId], viewModel.Roots.Select(static root => root.Id));
        Assert.Equal(WatchRootState.Synchronized.ToString(), viewModel.Roots[0].State);

        oldRuntime.Publish(
            SnapshotWithFirstState(oldConfiguration, WatchRootState.RescanRequired, 6),
            [FirstRootId]);
        dispatcher.RunAll();

        Assert.Equal(WatchRootState.RescanRequired.ToString(), viewModel.Roots[0].State);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task RuntimeStartFailureDisposesCandidateAndPreservesActiveRuntimeAndQueuedSnapshotFence()
    {
        ScriptedTextStore store = new(Found(ConfigurationJson()))
        {
            AllowWrites = true,
        };
        QueuedDispatcher dispatcher = new();
        RecordingRuntimeFactory factory = new()
        {
            RuntimeSetup = static (configuration, runtime) =>
            {
                if (configuration.WatchRoots.Count == 1)
                {
                    runtime.StartAction = () =>
                    {
                        runtime.Publish(
                            SnapshotWithFirstState(
                                configuration,
                                WatchRootState.RescanRequired,
                                1),
                            [SecondRootId]);
                        throw new InvalidOperationException("Start failed.");
                    };
                }
            },
        };
        MainWindowViewModel viewModel = CreateViewModel(store, factory, dispatcher);
        await viewModel.InitializeAsync();
        FakeRuntime oldRuntime = Assert.IsType<FakeRuntime>(factory.Runtime);
        OrganizationConfiguration oldConfiguration = factory.Configuration!;
        oldRuntime.Publish(
            SnapshotWithFirstState(oldConfiguration, WatchRootState.Synchronized, 5),
            [FirstRootId]);
        dispatcher.RunAll();

        viewModel.Editor.ConfigurationText = SingleRootConfigurationJson();
        await viewModel.Editor.SaveAsync(TestContext.Current.CancellationToken);

        FakeRuntime failedRuntime = factory.Runtimes[1];
        Assert.Equal(ConfigurationLoadState.RuntimeFailure, viewModel.LoadState);
        Assert.True(failedRuntime.Disposed);
        Assert.False(oldRuntime.Disposed);
        Assert.Equal([FirstRootId, SecondRootId], viewModel.Roots.Select(static root => root.Id));

        dispatcher.RunAll();

        Assert.Equal([FirstRootId, SecondRootId], viewModel.Roots.Select(static root => root.Id));
        oldRuntime.Publish(
            SnapshotWithFirstState(oldConfiguration, WatchRootState.RescanRequired, 6),
            [FirstRootId]);
        dispatcher.RunAll();
        Assert.Equal(WatchRootState.RescanRequired.ToString(), viewModel.Roots[0].State);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task CancellationDuringCandidateStartDisposesCandidateAndPreservesActiveRuntimeAndSnapshot()
    {
        using CancellationTokenSource cancellation = new();
        ScriptedTextStore store = new(Found(ConfigurationJson()))
        {
            AllowWrites = true,
        };
        QueuedDispatcher dispatcher = new();
        RecordingRuntimeFactory factory = new()
        {
            RuntimeSetup = (configuration, runtime) =>
            {
                if (configuration.WatchRoots.Count == 1)
                {
                    runtime.StartAction = cancellation.Cancel;
                }
            },
        };
        MainWindowViewModel viewModel = CreateViewModel(store, factory, dispatcher);
        await viewModel.InitializeAsync();
        FakeRuntime oldRuntime = Assert.IsType<FakeRuntime>(factory.Runtime);
        OrganizationConfiguration oldConfiguration = factory.Configuration!;
        oldRuntime.Publish(
            SnapshotWithFirstState(oldConfiguration, WatchRootState.Synchronized, 5),
            [FirstRootId]);
        dispatcher.RunAll();

        viewModel.Editor.ConfigurationText = SingleRootConfigurationJson();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => viewModel.Editor.SaveAsync(cancellation.Token));

        FakeRuntime cancelledRuntime = factory.Runtimes[1];
        Assert.True(cancelledRuntime.Disposed);
        Assert.False(oldRuntime.Disposed);
        Assert.Equal([FirstRootId, SecondRootId], viewModel.Roots.Select(static root => root.Id));
        Assert.Equal(WatchRootState.Synchronized.ToString(), viewModel.Roots[0].State);

        oldRuntime.Publish(
            SnapshotWithFirstState(oldConfiguration, WatchRootState.RescanRequired, 6),
            [FirstRootId]);
        dispatcher.RunAll();
        Assert.Equal(WatchRootState.RescanRequired.ToString(), viewModel.Roots[0].State);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task ShutdownObservesNonCancellationInitializationCleanupFailureWithoutFaultingInitializer()
    {
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        using ManualResetEventSlim startEntered = new();
        using ManualResetEventSlim releaseStart = new();
        ScriptedTextStore store = new(Found(ConfigurationJson()));
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
        ScriptedTextStore store = new(Found(ConfigurationJson()));
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

    [Fact]
    public async Task CleanCloseRequestDoesNotPromptOrWrite()
    {
        ScriptedTextStore store = new(Found(ConfigurationJson()));
        var prompt = new ScriptedUnsavedChangesPrompt(UnsavedChangesDecision.Discard);
        MainWindowViewModel viewModel = CreateViewModel(
            store,
            new RecordingRuntimeFactory(),
            new QueuedDispatcher(),
            prompt);
        await viewModel.InitializeAsync();

        bool closeApproved = await viewModel.RequestCloseAsync(
            TestContext.Current.CancellationToken);

        Assert.True(closeApproved);
        Assert.Equal(0, prompt.PromptCount);
        Assert.Equal(0, store.WriteCount);
        await viewModel.DisposeAsync();
    }

    [Theory]
    [InlineData(UnsavedChangesDecision.Cancel)]
    [InlineData((UnsavedChangesDecision)999)]
    public async Task CancelOrUnknownCloseDecisionKeepsDirtyWorkspaceOpen(
        UnsavedChangesDecision decision)
    {
        ScriptedTextStore store = new(Found(ConfigurationJson()));
        var prompt = new ScriptedUnsavedChangesPrompt(decision);
        MainWindowViewModel viewModel = CreateViewModel(
            store,
            new RecordingRuntimeFactory(),
            new QueuedDispatcher(),
            prompt);
        await viewModel.InitializeAsync();
        viewModel.Editor.ConfigurationText = SingleRootConfigurationJson();

        bool closeApproved = await viewModel.RequestCloseAsync(
            TestContext.Current.CancellationToken);

        Assert.False(closeApproved);
        Assert.True(viewModel.Editor.IsDirty);
        Assert.Equal(1, prompt.PromptCount);
        Assert.Equal(viewModel.ConfigurationPath, Assert.Single(prompt.ConfigurationPaths));
        Assert.Equal(0, store.WriteCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task DiscardCloseDecisionApprovesWithoutPersistingDraft()
    {
        ScriptedTextStore store = new(Found(ConfigurationJson()));
        var prompt = new ScriptedUnsavedChangesPrompt(UnsavedChangesDecision.Discard);
        MainWindowViewModel viewModel = CreateViewModel(
            store,
            new RecordingRuntimeFactory(),
            new QueuedDispatcher(),
            prompt);
        await viewModel.InitializeAsync();
        viewModel.Editor.ConfigurationText = SingleRootConfigurationJson();

        bool closeApproved = await viewModel.RequestCloseAsync(
            TestContext.Current.CancellationToken);

        Assert.True(closeApproved);
        Assert.True(viewModel.Editor.IsDirty);
        Assert.Equal(0, store.WriteCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task SaveCloseDecisionApprovesOnlyAfterDraftIsPersisted()
    {
        ScriptedTextStore store = new(Found(ConfigurationJson()))
        {
            AllowWrites = true,
        };
        var prompt = new ScriptedUnsavedChangesPrompt(UnsavedChangesDecision.Save);
        MainWindowViewModel viewModel = CreateViewModel(
            store,
            new RecordingRuntimeFactory(),
            new QueuedDispatcher(),
            prompt);
        await viewModel.InitializeAsync();
        viewModel.Editor.ConfigurationText = SingleRootConfigurationJson();

        bool closeApproved = await viewModel.RequestCloseAsync(
            TestContext.Current.CancellationToken);

        Assert.True(closeApproved);
        Assert.False(viewModel.Editor.IsDirty);
        Assert.Equal(1, store.WriteCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task InvalidDraftSaveCloseDecisionKeepsWorkspaceOpenWithoutWriting()
    {
        ScriptedTextStore store = new(Found(ConfigurationJson()));
        var prompt = new ScriptedUnsavedChangesPrompt(UnsavedChangesDecision.Save);
        MainWindowViewModel viewModel = CreateViewModel(
            store,
            new RecordingRuntimeFactory(),
            new QueuedDispatcher(),
            prompt);
        await viewModel.InitializeAsync();
        viewModel.Editor.ConfigurationText = "{ nope";

        bool closeApproved = await viewModel.RequestCloseAsync(
            TestContext.Current.CancellationToken);

        Assert.False(closeApproved);
        Assert.True(viewModel.Editor.IsDirty);
        Assert.Equal(ConfigurationLoadState.Invalid, viewModel.LoadState);
        Assert.Equal(0, store.WriteCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task StorageFailureDuringCloseSaveKeepsDirtyWorkspaceOpen()
    {
        ScriptedTextStore store = new(Found(ConfigurationJson()))
        {
            AllowWrites = true,
            WriteResult = ConfigurationTextWriteResult.Failed(
                [
                    new ConfigurationStorageProblem(
                        ConfigurationStorageProblemKind.PublicationFailure,
                        "Publication failed."),
                ]),
        };
        var prompt = new ScriptedUnsavedChangesPrompt(UnsavedChangesDecision.Save);
        MainWindowViewModel viewModel = CreateViewModel(
            store,
            new RecordingRuntimeFactory(),
            new QueuedDispatcher(),
            prompt);
        await viewModel.InitializeAsync();
        viewModel.Editor.ConfigurationText = SingleRootConfigurationJson();

        bool closeApproved = await viewModel.RequestCloseAsync(
            TestContext.Current.CancellationToken);

        Assert.False(closeApproved);
        Assert.True(viewModel.Editor.IsDirty);
        Assert.Equal(ConfigurationLoadState.StorageFailure, viewModel.LoadState);
        Assert.Equal(1, store.WriteCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task CandidateSnapshotPublishedBeforeSwapIsCaughtUpAfterCommit()
    {
        ScriptedTextStore store = new(Found(ConfigurationJson()));
        RecordingRuntimeFactory factory = new()
        {
            RuntimeSetup = static (configuration, runtime) =>
            {
                runtime.SnapshotReadAction = () => runtime.Publish(
                    SnapshotWithFirstState(
                        configuration,
                        WatchRootState.Synchronized,
                        1),
                    [FirstRootId]);
            },
        };
        MainWindowViewModel viewModel = CreateViewModel(
            store,
            factory,
            new InlineDispatcher());

        await viewModel.InitializeAsync();

        Assert.Equal(WatchRootState.Synchronized.ToString(), viewModel.Roots[0].State);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task PreviousRuntimeCleanupFailureDoesNotMisclassifyCommittedReplacement()
    {
        ScriptedTextStore store = new(Found(ConfigurationJson()))
        {
            AllowWrites = true,
        };
        RecordingRuntimeFactory factory = new();
        MainWindowViewModel viewModel = CreateViewModel(
            store,
            factory,
            new QueuedDispatcher());
        await viewModel.InitializeAsync();
        FakeRuntime previous = Assert.IsType<FakeRuntime>(factory.Runtime);
        previous.DisposeFailure = new IOException("Previous cleanup failed.");
        viewModel.Editor.ConfigurationText = SingleRootConfigurationJson();

        await viewModel.Editor.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.Loaded, viewModel.LoadState);
        Assert.False(viewModel.Editor.IsDirty);
        Assert.Equal(SecondRootId, Assert.Single(viewModel.Roots).Id);
        Assert.False(previous.Disposed);
        IOException cleanupFailure = await Assert.ThrowsAsync<IOException>(
            () => viewModel.DisposeAsync().AsTask());
        Assert.Equal("Previous cleanup failed.", cleanupFailure.Message);
    }

    [Fact]
    public async Task PostCommitRootsNotificationFailureDoesNotMisclassifyReplacement()
    {
        ScriptedTextStore store = new(Found(ConfigurationJson()))
        {
            AllowWrites = true,
        };
        RecordingRuntimeFactory factory = new();
        MainWindowViewModel viewModel = CreateViewModel(
            store,
            factory,
            new QueuedDispatcher());
        await viewModel.InitializeAsync();
        FakeRuntime previous = Assert.IsType<FakeRuntime>(factory.Runtime);
        bool rejectNextRootsNotification = true;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (rejectNextRootsNotification
                && eventArgs.PropertyName == nameof(MainWindowViewModel.Roots))
            {
                rejectNextRootsNotification = false;
                throw new InvalidOperationException("Presentation subscriber failed.");
            }
        };
        viewModel.Editor.ConfigurationText = SingleRootConfigurationJson();

        await viewModel.Editor.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.Loaded, viewModel.LoadState);
        Assert.False(viewModel.Editor.IsDirty);
        Assert.Equal(SecondRootId, Assert.Single(viewModel.Roots).Id);
        Assert.True(previous.Disposed);
        await viewModel.DisposeAsync();
    }

    private static MainWindowViewModel CreateViewModel(
        ScriptedTextStore store,
        RecordingRuntimeFactory factory,
        IUiDispatcher dispatcher,
        IUnsavedChangesPrompt? unsavedChangesPrompt = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "file-organizer-tests", "config.json");
        return new MainWindowViewModel(
            ConfigurationPathArguments.Parse(["--config", path]),
            new OrganizationConfigurationService(store),
            factory,
            dispatcher,
            new NullPathPicker(),
            unsavedChangesPrompt ?? new ScriptedUnsavedChangesPrompt());
    }

    private static ConfigurationTextReadResult Found(string content) =>
        ConfigurationTextReadResult.Found(
            content,
            ConfigurationDocumentRevision.FromBytes(Encoding.UTF8.GetBytes(content)));

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

    private static string SingleRootConfigurationJson() => $$"""
        {
          "schemaVersion": 1,
          "pathPolicy": {
            "syntax": "windows",
            "caseSensitivity": "insensitive",
            "unicodeNormalization": "formC"
          },
          "watchRoots": [
            {
              "id": "{{SecondRootId:D}}",
              "rootPath": "D:\\WatchB",
              "enabled": true,
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

        public bool AllowWrites { get; init; }

        public ConfigurationTextWriteResult? WriteResult { get; init; }

        public ValueTask<ConfigurationTextReadResult> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(readResult);
        }

        public ValueTask<ConfigurationTextWriteResult> WriteAsync(
            ConfigurationTextWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            if (!AllowWrites)
            {
                throw new InvalidOperationException(
                    "This test did not authorize a configuration write.");
            }

            return ValueTask.FromResult(
                WriteResult
                ?? ConfigurationTextWriteResult.Succeeded(
                    ConfigurationDocumentRevision.FromBytes(
                        Encoding.UTF8.GetBytes(request.Content))));
        }
    }

    private sealed class RecordingRuntimeFactory : IWatchPreviewRuntimeFactory
    {
        public int CreateCount { get; private set; }

        public OrganizationConfiguration? Configuration { get; private set; }

        public IWatchPreviewRuntime? Runtime { get; private set; }

        public List<FakeRuntime> Runtimes { get; } = [];

        public Action<OrganizationConfiguration, FakeRuntime>? RuntimeSetup { get; init; }

        public Func<OrganizationConfiguration, Exception?>? CreateFailure { get; init; }

        public IWatchPreviewRuntime Create(OrganizationConfiguration configuration)
        {
            CreateCount++;
            Exception? failure = CreateFailure?.Invoke(configuration);
            if (failure is not null)
            {
                throw failure;
            }

            Configuration = configuration;
            var runtime = new FakeRuntime(InitialSnapshot(configuration));
            RuntimeSetup?.Invoke(configuration, runtime);
            Runtimes.Add(runtime);
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
        private WatchPreviewRuntimeSnapshot _snapshot = snapshot;
        private WatchPreviewRuntimeSnapshot? _startNotification;
        private WatchPreviewRuntimeSnapshot? _postStartSnapshot;
        private Guid[]? _startChangedRootIds;

        public event EventHandler<WatchPreviewRuntimeNotification>? SnapshotChanged;

        public WatchPreviewRuntimeSnapshot Snapshot
        {
            get
            {
                WatchPreviewRuntimeSnapshot captured = _snapshot;
                Action? action = SnapshotReadAction;
                SnapshotReadAction = null;
                action?.Invoke();
                return captured;
            }
            private set => _snapshot = value;
        }

        public List<Guid> Paused { get; } = [];

        public List<Guid> Resumed { get; } = [];

        public List<Guid> Reconnected { get; } = [];

        public List<Guid> BaselineRequests { get; } = [];

        public bool Disposed { get; private set; }

        public bool DisposalStarted { get; private set; }

        public Action? StartAction { get; set; }

        public Action? SnapshotReadAction { get; set; }

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

        public bool ReconnectRoot(Guid watchRootId)
        {
            Reconnected.Add(watchRootId);
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

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class NullPathPicker : IConfigurationPathPicker
    {
        public ValueTask<string?> OpenExistingAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask<string?> ChooseSaveDestinationAsync(
            string suggestedFileName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(null);
        }
    }

    private sealed class ScriptedUnsavedChangesPrompt(
        UnsavedChangesDecision decision = UnsavedChangesDecision.Cancel)
        : IUnsavedChangesPrompt
    {
        public int PromptCount { get; private set; }

        public List<string> ConfigurationPaths { get; } = [];

        public ValueTask<UnsavedChangesDecision> PromptAsync(
            string configurationPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PromptCount++;
            ConfigurationPaths.Add(configurationPath);
            return ValueTask.FromResult(decision);
        }
    }
}
