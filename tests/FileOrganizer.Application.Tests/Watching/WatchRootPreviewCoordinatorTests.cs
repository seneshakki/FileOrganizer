using System.Threading.Channels;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Application.Watching;
using FileOrganizer.Core.Abstractions;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Rules;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Application.Tests.Watching;

public sealed class WatchRootPreviewCoordinatorTests
{
    private static readonly Guid RootId =
        Guid.Parse("33333333-bbbb-4444-8888-333333333333");

    private static readonly Guid RuleId =
        Guid.Parse("44444444-bbbb-4444-8888-444444444444");

    private static readonly DateTimeOffset Now =
        new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly PathPolicy Paths = new(
        PathSyntax.Windows,
        PathCaseSensitivity.Insensitive,
        PathUnicodeNormalization.FormC);

    [Fact]
    public async Task CompleteBaselinePublishesAFullRootPreviewAndSynchronizesMonitor()
    {
        await using Harness harness = new(includeSubdirectories: true);
        harness.Scanner.Current = CompleteScan(File("one.txt", "one", 7));

        harness.Coordinator.Start();
        WatchRootPreviewResult result = await harness.NextResultAsync();

        Assert.Equal(WatchRootPreviewOutcome.Ready, result.Outcome);
        Assert.Equal(WatchRootPreviewTrigger.Baseline, result.Trigger);
        Assert.NotNull(result.SnapshotToken);
        Assert.Equal(WatchRootState.Synchronized, harness.Monitor.State);
        Assert.Single(result.Plan!.Items);
        Assert.Equal(PlanItemOutcome.Ready, result.Plan.Items[0].Outcome);
        Assert.True(result.Plan.IsEligibleForExecution);
        Assert.Equal([true], harness.Scanner.RecursiveArguments);
        Assert.Equal(1, harness.DestinationInspector.CallCount);
    }

    [Fact]
    public async Task StartupStateNotificationCreatesExactlyOneBaselineRequest()
    {
        await using Harness harness = new();
        harness.Scanner.Current = CompleteScan();

        harness.Coordinator.Start();
        WatchRootPreviewResult result = await harness.NextResultAsync();
        await harness.Coordinator.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WatchRootPreviewOutcome.Ready, result.Outcome);
        Assert.Equal(1, harness.Scanner.CallCount);
        Assert.False(harness.TryTakeResult(out _));
    }

    [Fact]
    public async Task StartingAgainstAPreSynchronizedMonitorEstablishesCoordinatorBaseline()
    {
        await using Harness harness = new();
        harness.Scanner.Current = CompleteScan();
        harness.Monitor.Start();
        WatchBaselineToken priorBaseline = harness.Monitor.CaptureBaselineToken();
        Assert.True(harness.Monitor.AcknowledgeBaseline(priorBaseline));

        harness.Coordinator.Start();
        WatchRootPreviewResult result = await harness.NextResultAsync();
        await harness.Coordinator.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WatchRootPreviewOutcome.Ready, result.Outcome);
        Assert.Equal(WatchRootPreviewTrigger.Baseline, result.Trigger);
        Assert.Equal(WatchRootState.Synchronized, harness.Monitor.State);
        Assert.Equal(1, harness.Scanner.CallCount);
    }

    [Fact]
    public async Task IncompleteBaselineRemainsUntrustedAndDoesNotPublishAPlan()
    {
        await using Harness harness = new();
        harness.Scanner.Current = new FileScanResult(
            Canonical(@"C:\Watch"),
            [],
            [new ScanDiagnostic(ScanDiagnosticKind.AccessDenied, @"C:\Watch\private")],
            RootIdentity("root"));

        harness.Coordinator.Start();
        WatchRootPreviewResult result = await harness.NextResultAsync();

        Assert.Equal(WatchRootPreviewOutcome.RescanRequired, result.Outcome);
        Assert.Equal(
            WatchRootPreviewProblemKind.IncompleteSourceScan,
            Assert.Single(result.Problems).Kind);
        Assert.Null(result.Plan);
        Assert.Equal(WatchRootState.RequiresInitialScan, harness.Monitor.State);
        Assert.Equal(0, harness.DestinationInspector.CallCount);
    }

    [Fact]
    public async Task BaselineWithoutStrongDirectoryIdentityRemainsUntrusted()
    {
        await using Harness harness = new();
        harness.Scanner.Current = new FileScanResult(Canonical(@"C:\Watch"), [], []);

        harness.Coordinator.Start();
        WatchRootPreviewResult result = await harness.NextResultAsync();

        Assert.Equal(WatchRootPreviewOutcome.RescanRequired, result.Outcome);
        Assert.Equal(
            WatchRootPreviewProblemKind.MissingSourceRootIdentity,
            Assert.Single(result.Problems).Kind);
        Assert.Equal(WatchRootState.RequiresInitialScan, harness.Monitor.State);
    }

    [Fact]
    public async Task EventDuringBaselineSuppressesTheStalePlan()
    {
        await using Harness harness = new();
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        harness.Scanner.Enqueue(cancellationToken =>
        {
            entered.TrySetResult();
            release.Task.Wait(cancellationToken);
            return CompleteScan();
        });
        harness.Scanner.Current = CompleteScan();

        harness.Coordinator.Start();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        harness.Source.Raise(new FileChangeSourceEvent(
            FileChangeSourceEventKind.Changed,
            sequence: 1,
            path: @"C:\Watch\during.txt"));
        release.TrySetResult();

        WatchRootPreviewResult result = await harness.NextResultAsync();
        Assert.Equal(WatchRootPreviewOutcome.Suppressed, result.Outcome);
        Assert.Equal(
            WatchRootPreviewProblemKind.StaleSnapshot,
            Assert.Single(result.Problems).Kind);
        Assert.Null(result.Plan);

        WatchRootPreviewResult replacement = await harness.NextResultAsync();
        await harness.Coordinator.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WatchRootPreviewOutcome.Ready, replacement.Outcome);
        Assert.Equal(WatchRootPreviewTrigger.Baseline, replacement.Trigger);
        Assert.Equal(2, harness.Scanner.CallCount);
        Assert.False(harness.TryTakeResult(out _));
    }

    [Fact]
    public async Task StableCandidateTriggersFullScanAndRequiresAnExactCandidateSnapshot()
    {
        await using Harness harness = new();
        harness.Scanner.Current = CompleteScan();
        harness.Coordinator.Start();
        _ = await harness.NextResultAsync();

        FileEntry file = File("ready.txt", "ready", 11);
        harness.Scanner.Current = CompleteScan(file);
        harness.Observations.Set(file);
        harness.Source.Raise(new FileChangeSourceEvent(
            FileChangeSourceEventKind.Created,
            sequence: 1,
            path: file.FullPath));

        WatchRootPreviewResult result = await harness.NextResultAsync();
        Assert.Equal(WatchRootPreviewOutcome.Ready, result.Outcome);
        Assert.Equal(WatchRootPreviewTrigger.StableCandidate, result.Trigger);
        Assert.Equal(file.PhysicalIdentity, result.Candidate!.Identity);
        Assert.Equal(file.FullPath, Assert.Single(result.Plan!.Items).Source.FullPath);
        Assert.Equal(2, harness.Scanner.CallCount);
    }

    [Fact]
    public async Task CandidateRefreshRetainsWholeRootConflictAnalysis()
    {
        await using Harness harness = new(includeSubdirectories: true);
        harness.Scanner.Current = CompleteScan();
        harness.Coordinator.Start();
        _ = await harness.NextResultAsync();

        FileEntry candidate = File(@"first\same.txt", "first", 4);
        FileEntry unrelated = File(@"second\same.txt", "second", 5);
        harness.Scanner.Current = CompleteScan(candidate, unrelated);
        harness.Observations.Set(candidate);
        harness.Source.Raise(new FileChangeSourceEvent(
            FileChangeSourceEventKind.Created,
            sequence: 1,
            path: candidate.FullPath));

        WatchRootPreviewResult result = await harness.NextResultAsync();

        Assert.Equal(WatchRootPreviewOutcome.Ready, result.Outcome);
        Assert.Equal(2, result.Plan!.Items.Count);
        Assert.All(
            result.Plan.Items,
            item => Assert.Equal(PlanItemOutcome.Conflict, item.Outcome));
        Assert.False(result.Plan.IsEligibleForExecution);
    }

    [Fact]
    public async Task CandidateIdentityDriftInvalidatesSynchronization()
    {
        await using Harness harness = new();
        harness.Scanner.Current = CompleteScan();
        harness.Coordinator.Start();
        _ = await harness.NextResultAsync();

        FileEntry observed = File("changed.txt", "first", 11);
        harness.Observations.Set(observed);
        harness.Scanner.Current = CompleteScan(File("changed.txt", "second", 11));
        harness.Source.Raise(new FileChangeSourceEvent(
            FileChangeSourceEventKind.Created,
            sequence: 1,
            path: observed.FullPath));

        WatchRootPreviewResult result = await harness.NextResultAsync();
        Assert.Equal(WatchRootPreviewOutcome.RescanRequired, result.Outcome);
        Assert.Equal(
            WatchRootPreviewProblemKind.CandidateChanged,
            Assert.Single(result.Problems).Kind);
        Assert.Null(result.Plan);
    }

    [Fact]
    public async Task SourceRootIdentityDriftInvalidatesSynchronization()
    {
        await using Harness harness = new();
        harness.Scanner.Current = CompleteScan(rootIdentity: RootIdentity("original"));
        harness.Coordinator.Start();
        _ = await harness.NextResultAsync();

        FileEntry file = File("root-drift.txt", "file", 3);
        harness.Observations.Set(file);
        harness.Scanner.Current = CompleteScan(
            [file],
            RootIdentity("replacement"));
        harness.Source.Raise(new FileChangeSourceEvent(
            FileChangeSourceEventKind.Created,
            sequence: 1,
            path: file.FullPath));

        WatchRootPreviewResult result = await harness.NextResultAsync();
        Assert.Equal(WatchRootPreviewOutcome.RescanRequired, result.Outcome);
        Assert.Equal(
            WatchRootPreviewProblemKind.SourceRootIdentityChanged,
            Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public async Task ScannerFailureIsStructuredAndNeverReachesDestinationInspection()
    {
        await using Harness harness = new();
        harness.Scanner.Enqueue(static _ => throw new IOException("scripted"));

        harness.Coordinator.Start();
        WatchRootPreviewResult result = await harness.NextResultAsync();

        Assert.Equal(WatchRootPreviewOutcome.Failed, result.Outcome);
        Assert.Equal(
            WatchRootPreviewProblemKind.SourceScanFailed,
            Assert.Single(result.Problems).Kind);
        Assert.Equal(0, harness.DestinationInspector.CallCount);
        Assert.Null(result.Plan);
    }

    [Fact]
    public async Task TransientBaselineFailureCanBeRetriedExplicitlyOnce()
    {
        await using Harness harness = new();
        harness.Scanner.Enqueue(static _ => throw new IOException("scripted"));
        harness.Scanner.Current = CompleteScan();

        harness.Coordinator.Start();
        WatchRootPreviewResult failure = await harness.NextResultAsync();
        Assert.Equal(WatchRootPreviewOutcome.Failed, failure.Outcome);

        Assert.True(harness.Coordinator.RequestBaselineScan());
        WatchRootPreviewResult retry = await harness.NextResultAsync();

        Assert.Equal(WatchRootPreviewOutcome.Ready, retry.Outcome);
        Assert.Equal(WatchRootState.Synchronized, harness.Monitor.State);
        Assert.Equal(2, harness.Scanner.CallCount);
        Assert.False(harness.Coordinator.RequestBaselineScan());
    }

    [Fact]
    public async Task ExplicitRetryCoalescesWithAnActiveInitialBaseline()
    {
        await using Harness harness = new();
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        harness.Scanner.Enqueue(cancellationToken =>
        {
            entered.TrySetResult();
            release.Task.Wait(cancellationToken);
            return CompleteScan();
        });

        harness.Coordinator.Start();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(harness.Coordinator.RequestBaselineScan());
        release.TrySetResult();

        WatchRootPreviewResult result = await harness.NextResultAsync();
        await harness.Coordinator.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WatchRootPreviewOutcome.Ready, result.Outcome);
        Assert.Equal(1, harness.Scanner.CallCount);
        Assert.False(harness.TryTakeResult(out _));
    }

    [Fact]
    public async Task DestinationInspectorFailureDoesNotAcknowledgeBaseline()
    {
        await using Harness harness = new();
        harness.Scanner.Current = CompleteScan(File("inspect.txt", "inspect", 2));
        harness.DestinationInspector.Exception = new IOException("scripted");

        harness.Coordinator.Start();
        WatchRootPreviewResult result = await harness.NextResultAsync();

        Assert.Equal(WatchRootPreviewOutcome.Failed, result.Outcome);
        Assert.Equal(
            WatchRootPreviewProblemKind.DestinationInspectionFailed,
            Assert.Single(result.Problems).Kind);
        Assert.Equal(WatchRootState.RequiresInitialScan, harness.Monitor.State);
    }

    [Fact]
    public async Task DeletionOfAProvenCandidateRequestsAFullRootRefresh()
    {
        await using Harness harness = new();
        harness.Scanner.Current = CompleteScan();
        harness.Coordinator.Start();
        _ = await harness.NextResultAsync();

        FileEntry file = File("removed.txt", "removed", 6);
        harness.Scanner.Current = CompleteScan(file);
        harness.Observations.Set(file);
        harness.Source.Raise(new FileChangeSourceEvent(
            FileChangeSourceEventKind.Created,
            sequence: 1,
            path: file.FullPath));
        _ = await harness.NextResultAsync();

        harness.Scanner.Current = CompleteScan();
        harness.Source.Raise(new FileChangeSourceEvent(
            FileChangeSourceEventKind.Deleted,
            sequence: 2,
            path: file.FullPath));

        WatchRootPreviewResult result = await harness.NextResultAsync();
        Assert.Equal(WatchRootPreviewOutcome.Ready, result.Outcome);
        Assert.Equal(WatchRootPreviewTrigger.RefreshRequested, result.Trigger);
        Assert.Equal(file.Path.Value, result.AffectedPath!.Value);
        Assert.Empty(result.Plan!.Items);
    }

    [Fact]
    public async Task EventDuringCandidateScanSuppressesItsOldSnapshot()
    {
        await using Harness harness = new();
        harness.Scanner.Current = CompleteScan();
        harness.Coordinator.Start();
        _ = await harness.NextResultAsync();

        FileEntry file = File("stale.txt", "stale", 8);
        harness.Observations.Set(file);
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        harness.Scanner.Enqueue(cancellationToken =>
        {
            entered.TrySetResult();
            release.Task.Wait(cancellationToken);
            return CompleteScan(file);
        });
        harness.Scanner.Current = CompleteScan(file);
        harness.Source.Raise(new FileChangeSourceEvent(
            FileChangeSourceEventKind.Created,
            sequence: 1,
            path: file.FullPath));
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        harness.Source.Raise(new FileChangeSourceEvent(
            FileChangeSourceEventKind.Changed,
            sequence: 2,
            path: file.FullPath));
        release.TrySetResult();

        WatchRootPreviewResult result = await harness.NextResultAsync();
        Assert.Equal(WatchRootPreviewOutcome.Suppressed, result.Outcome);
        Assert.Equal(
            WatchRootPreviewProblemKind.StaleSnapshot,
            Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public async Task ActiveRefreshCoalescesToTheLatestPendingCandidate()
    {
        await using Harness harness = new();
        harness.Scanner.Current = CompleteScan();
        harness.Coordinator.Start();
        _ = await harness.NextResultAsync();

        FileEntry first = File("first.txt", "first", 1);
        FileEntry second = File("second.txt", "second", 2);
        FileEntry latest = File("latest.txt", "latest", 3);
        harness.Observations.Set(first);
        harness.Observations.Set(second);
        harness.Observations.Set(latest);
        TaskCompletionSource latestCandidatePublished = NewSignal();
        harness.Monitor.Notification += (_, notification) =>
        {
            if (notification.Kind == WatchRootNotificationKind.Candidate
                && notification.Candidate?.Path.Value == latest.Path.Value)
            {
                latestCandidatePublished.TrySetResult();
            }
        };

        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        harness.Scanner.Enqueue(cancellationToken =>
        {
            entered.TrySetResult();
            release.Task.Wait(cancellationToken);
            return CompleteScan(first);
        });
        harness.Scanner.Current = CompleteScan(first, second, latest);
        harness.Source.Raise(new FileChangeSourceEvent(
            FileChangeSourceEventKind.Created,
            sequence: 1,
            path: first.FullPath));
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        harness.Source.Raise(new FileChangeSourceEvent(
            FileChangeSourceEventKind.Created,
            sequence: 2,
            path: second.FullPath));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        harness.Source.Raise(new FileChangeSourceEvent(
            FileChangeSourceEventKind.Created,
            sequence: 3,
            path: latest.FullPath));
        await latestCandidatePublished.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.TrySetResult();

        WatchRootPreviewResult stale = await harness.NextResultAsync();
        WatchRootPreviewResult replacement = await harness.NextResultAsync();
        await harness.Coordinator.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WatchRootPreviewOutcome.Suppressed, stale.Outcome);
        Assert.Equal(WatchRootPreviewOutcome.Ready, replacement.Outcome);
        Assert.Equal(latest.Path.Value, replacement.Candidate!.Path.Value);
        Assert.Equal(3, replacement.Plan!.Items.Count);
        Assert.Equal(3, harness.Scanner.CallCount);
        Assert.False(harness.TryTakeResult(out _));
    }

    [Fact]
    public async Task PauseClearsQueuedRefreshAndDrainCompletesAfterActiveScan()
    {
        await using Harness harness = new();
        harness.Scanner.Current = CompleteScan();
        harness.Coordinator.Start();
        _ = await harness.NextResultAsync();

        FileEntry activeFile = File("active.txt", "active", 4);
        FileEntry queuedFile = File("queued.txt", "queued", 5);
        harness.Observations.Set(activeFile);
        harness.Observations.Set(queuedFile);
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        harness.Scanner.Enqueue(cancellationToken =>
        {
            entered.TrySetResult();
            release.Task.Wait(cancellationToken);
            return CompleteScan(activeFile);
        });
        harness.Scanner.Current = CompleteScan(queuedFile);

        harness.Source.Raise(new FileChangeSourceEvent(
            FileChangeSourceEventKind.Created,
            sequence: 1,
            path: activeFile.FullPath));
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        TaskCompletionSource queued = NewSignal();
        EventHandler<WatchRootNotification> observeQueued = (_, notification) =>
        {
            if (notification.Candidate?.Path.Value == queuedFile.FullPath)
            {
                queued.TrySetResult();
            }
        };
        harness.Monitor.Notification += observeQueued;
        try
        {
            harness.Source.Raise(new FileChangeSourceEvent(
                FileChangeSourceEventKind.Created,
                sequence: 2,
                path: queuedFile.FullPath));
            await queued.Task.WaitAsync(TestContext.Current.CancellationToken);

            harness.Monitor.Pause();
            release.TrySetResult();
            _ = await harness.NextResultAsync();
            await harness.Coordinator.DrainAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, harness.Scanner.CallCount);
            Assert.False(harness.TryTakeResult(out _));
        }
        finally
        {
            harness.Monitor.Notification -= observeQueued;
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task ConstructorRejectsAConfigurationForAnotherLogicalScope()
    {
        WatchRootConfiguration monitorRoot = new(
            RootId,
            Canonical(@"C:\Watch"),
            isEnabled: true,
            includeSubdirectories: true);
        await using WatchRootMonitor monitor = CreateMonitor(monitorRoot, out _, out _);
        OrganizationConfiguration mismatched = Configuration(
            new WatchRootConfiguration(
                RootId,
                Canonical(@"D:\Other"),
                isEnabled: true,
                includeSubdirectories: true));

        Assert.Throws<ArgumentException>(
            () => new WatchRootPreviewCoordinator(
                monitor,
                mismatched,
                new ScriptedScanner(CompleteScan()),
                new ScriptedDestinationInspector(),
                new OrganizationPlanner(new FixedTimeProvider(Now))));
    }

    [Fact]
    public async Task ConcurrentDisposeCallsJoinTheSameBlockedWorkerShutdown()
    {
        await using Harness harness = new();
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        harness.Scanner.Enqueue(_ =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return CompleteScan();
        });
        harness.Coordinator.Start();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Task first = harness.Coordinator.DisposeAsync().AsTask();
        Task second = harness.Coordinator.DisposeAsync().AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TestContext.Current.CancellationToken);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static FileScanResult CompleteScan(params FileEntry[] files) =>
        CompleteScan(files, RootIdentity("root"));

    private static FileScanResult CompleteScan(
        IEnumerable<FileEntry> files,
        PhysicalEntryIdentity rootIdentity) =>
        new(Canonical(@"C:\Watch"), files, [], rootIdentity);

    private static FileScanResult CompleteScan(PhysicalEntryIdentity rootIdentity) =>
        CompleteScan([], rootIdentity);

    private static FileEntry File(string name, string identity, long length) =>
        new(
            Canonical(@"C:\Watch\" + name),
            length,
            Now.AddDays(-2),
            Now.AddMinutes(-1),
            FileIdentity(identity));

    private static PhysicalEntryIdentity FileIdentity(string identity) =>
        new("test", 1, "volume", identity, PhysicalEntryKind.RegularFile);

    private static PhysicalEntryIdentity RootIdentity(string identity) =>
        new("test", 1, "volume", identity, PhysicalEntryKind.Directory);

    private static CanonicalPath Canonical(string path) =>
        Paths.CanonicalizeAbsolute(path).Path
        ?? throw new InvalidOperationException($"Invalid test path: {path}");

    private static OrganizationConfiguration Configuration(WatchRootConfiguration root)
    {
        OrganizationRule rule = new(
            new Rule("all files", AlwaysMatchCondition.Instance),
            new MoveToDirectoryAction(@"C:\Destination"));
        return new OrganizationConfiguration(
            Paths.Semantics,
            [new ConfiguredOrganizationRule(RuleId, rule)],
            [root]);
    }

    private static WatchRootMonitor CreateMonitor(
        WatchRootConfiguration root,
        out ScriptedSourceFactory factory,
        out ScriptedObservationPort observations)
    {
        factory = new ScriptedSourceFactory();
        observations = new ScriptedObservationPort();
        return new WatchRootMonitor(
            root,
            factory,
            observations,
            new ImmediateDelay(),
            new FixedTimeProvider(Now),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Channel<WatchRootPreviewResult> _results =
            Channel.CreateUnbounded<WatchRootPreviewResult>();

        public Harness(bool includeSubdirectories = true)
        {
            WatchRootConfiguration root = new(
                RootId,
                Canonical(@"C:\Watch"),
                isEnabled: true,
                includeSubdirectories);
            Monitor = CreateMonitor(
                root,
                out ScriptedSourceFactory factory,
                out ScriptedObservationPort observations);
            Factory = factory;
            Scanner = new ScriptedScanner(CompleteScan());
            DestinationInspector = new ScriptedDestinationInspector();
            Observations = observations;
            Coordinator = new WatchRootPreviewCoordinator(
                Monitor,
                Configuration(root),
                Scanner,
                DestinationInspector,
                new OrganizationPlanner(new FixedTimeProvider(Now)));
            Coordinator.PreviewProduced += (_, result) => _results.Writer.TryWrite(result);
        }

        public WatchRootMonitor Monitor { get; }

        public WatchRootPreviewCoordinator Coordinator { get; }

        public ScriptedSourceFactory Factory { get; }

        public ScriptedScanner Scanner { get; }

        public ScriptedDestinationInspector DestinationInspector { get; }

        public ScriptedObservationPort Observations { get; }

        public ScriptedSource Source => Factory.Source;

        public ValueTask<WatchRootPreviewResult> NextResultAsync() =>
            _results.Reader.ReadAsync(TestContext.Current.CancellationToken);

        public bool TryTakeResult(out WatchRootPreviewResult? result) =>
            _results.Reader.TryRead(out result);

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Monitor.DisposeAsync();
        }

    }

    private sealed class ScriptedScanner(FileScanResult current) : IFileScanner
    {
        private readonly object _gate = new();
        private readonly Queue<Func<CancellationToken, FileScanResult>> _responses = [];
        private FileScanResult _current = current;

        public int CallCount { get; private set; }

        public List<bool> RecursiveArguments { get; } = [];

        public FileScanResult Current
        {
            get
            {
                lock (_gate)
                {
                    return _current;
                }
            }

            set
            {
                ArgumentNullException.ThrowIfNull(value);
                lock (_gate)
                {
                    _current = value;
                }
            }
        }

        public void Enqueue(Func<CancellationToken, FileScanResult> response)
        {
            ArgumentNullException.ThrowIfNull(response);
            lock (_gate)
            {
                _responses.Enqueue(response);
            }
        }

        public FileScanResult Scan(
            CanonicalPath sourceRoot,
            bool recursive,
            CancellationToken cancellationToken = default)
        {
            Func<CancellationToken, FileScanResult>? response;
            FileScanResult currentResult;
            lock (_gate)
            {
                CallCount++;
                RecursiveArguments.Add(recursive);
                response = _responses.Count == 0 ? null : _responses.Dequeue();
                currentResult = _current;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return response?.Invoke(cancellationToken) ?? currentResult;
        }
    }

    private sealed class ScriptedDestinationInspector : IDestinationStateInspector
    {
        public int CallCount { get; private set; }

        public Exception? Exception { get; set; }

        public DestinationStateSnapshot Inspect(
            OrganizationPlanPreparation preparation,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (Exception is not null)
            {
                throw Exception;
            }

            var policy = new PathPolicy(
                preparation.PathSemantics.Syntax,
                preparation.PathSemantics.CaseSensitivity,
                preparation.PathSemantics.UnicodeNormalization);
            HashSet<CanonicalPath> directories = new(
                preparation.DestinationDirectories,
                policy.LogicalComparer);
            return new DestinationStateSnapshot(
                preparation.PathSemantics,
                preparation.DestinationPaths.Select(
                    path => directories.Contains(path)
                        ? new DestinationPathFact(
                            path,
                            DestinationEntryKind.Directory,
                            physicalIdentity: new PhysicalEntryIdentity(
                                "test",
                                1,
                                "volume",
                                path.Value,
                                PhysicalEntryKind.Directory))
                        : new DestinationPathFact(path, DestinationEntryKind.Missing)),
                cancellationToken);
        }
    }

    private sealed class ScriptedSourceFactory : IFileChangeSourceFactory
    {
        public ScriptedSource Source { get; } = new();

        public FileChangeSourceOpenResult Open(FileChangeSourceRequest request) =>
            FileChangeSourceOpenResult.Opened(Source);
    }

    private sealed class ScriptedSource : IFileChangeSource
    {
        public event EventHandler<FileChangeSourceEvent>? EventReceived;

        public bool IsStarted { get; private set; }

        public void Start() => IsStarted = true;

        public void Raise(FileChangeSourceEvent change)
        {
            Assert.True(IsStarted);
            EventReceived?.Invoke(this, change);
        }

        public void Dispose()
        {
            IsStarted = false;
            EventReceived = null;
        }
    }

    private sealed class ScriptedObservationPort : IFileStabilityObservationPort
    {
        private readonly object _gate = new();
        private readonly Dictionary<CanonicalPath, PhysicalEntryObservation> _observations =
            new(Paths.LogicalComparer);

        public void Set(FileEntry file)
        {
            lock (_gate)
            {
                _observations[file.Path] = PhysicalEntryObservation.Success(
                    file.PhysicalIdentity!,
                    new PhysicalFileMetadata(
                        file.SizeInBytes,
                        file.CreatedUtc,
                        file.ModifiedUtc));
            }
        }

        public ValueTask<PhysicalEntryObservation> ObserveAsync(
            CanonicalPath path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTask.FromResult(
                    _observations.TryGetValue(path, out PhysicalEntryObservation? observation)
                        ? observation
                        : PhysicalEntryObservation.Missing());
            }
        }
    }

    private sealed class ImmediateDelay : IWatchDelay
    {
        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AlwaysMatchCondition : ICondition
    {
        public static AlwaysMatchCondition Instance { get; } = new();

        public ConditionResult Evaluate(FileEntry file, EvaluationContext context) =>
            ConditionResult.Match;

        public IEnumerable<EvaluationProblem> Validate() => [];
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
