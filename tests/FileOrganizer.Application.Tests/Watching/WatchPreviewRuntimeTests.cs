using System.Collections.Concurrent;
using System.Diagnostics;
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

public sealed class WatchPreviewRuntimeTests
{
    private static readonly Guid FirstRootId =
        Guid.Parse("11111111-aaaa-4444-8888-111111111111");

    private static readonly Guid SecondRootId =
        Guid.Parse("22222222-bbbb-4444-8888-222222222222");

    private static readonly Guid ThirdRootId =
        Guid.Parse("33333333-cccc-4444-8888-333333333333");

    private static readonly Guid FourthRootId =
        Guid.Parse("44444444-dddd-4444-8888-444444444444");

    private static readonly Guid RuleId =
        Guid.Parse("aaaaaaaa-1111-4444-8888-aaaaaaaaaaaa");

    private static readonly DateTimeOffset Now =
        new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static readonly PathPolicy Paths = new(
        PathSyntax.Windows,
        PathCaseSensitivity.Insensitive,
        PathUnicodeNormalization.FormC);

    [Fact]
    public void OptionsRequirePositiveTimingAndConcurrencyBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WatchPreviewRuntimeOptions(
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                maxConcurrentScans: 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WatchPreviewRuntimeOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                maxConcurrentScans: 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WatchPreviewRuntimeOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                maxConcurrentScans: 0));

        WatchPreviewRuntimeOptions options = new(
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(50),
            maxConcurrentScans: 3);

        Assert.Equal(TimeSpan.FromMilliseconds(25), options.DebounceWindow);
        Assert.Equal(TimeSpan.FromMilliseconds(50), options.StabilityInterval);
        Assert.Equal(3, options.MaxConcurrentScans);
    }

    [Fact]
    public async Task StartPreservesConfiguredOrderAndNeverOpensOrScansDisabledRoots()
    {
        WatchRootConfiguration disabled = Root(
            SecondRootId,
            @"C:\Watch-B",
            isEnabled: false,
            includeSubdirectories: false);
        WatchRootConfiguration first = Root(
            FirstRootId,
            @"C:\Watch-A",
            isEnabled: true,
            includeSubdirectories: true);
        WatchRootConfiguration third = Root(
            ThirdRootId,
            @"C:\Watch-C",
            isEnabled: true,
            includeSubdirectories: false);
        await using Harness harness = new([disabled, first, third]);

        WatchPreviewRuntimeSnapshot beforeStart = harness.Runtime.Snapshot;
        harness.Runtime.Start();
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);
        WatchPreviewRuntimeSnapshot afterStart = harness.Runtime.Snapshot;

        Assert.Equal(
            [SecondRootId, FirstRootId, ThirdRootId],
            afterStart.Roots.Select(static root => root.WatchRootId));
        Assert.Equal(
            [FirstRootId, ThirdRootId],
            harness.SourceFactory.Requests.Select(static request => request.WatchRootId));
        Assert.Equal(0, harness.Scanner.CallCount(SecondRootId));
        Assert.Equal(1, harness.Scanner.CallCount(FirstRootId));
        Assert.Equal(1, harness.Scanner.CallCount(ThirdRootId));
        Assert.True(harness.Scanner.RecursiveArguments(FirstRootId).Single());
        Assert.False(harness.Scanner.RecursiveArguments(ThirdRootId).Single());

        WatchPreviewRootSnapshot disabledSnapshot = RootSnapshot(afterStart, SecondRootId);
        Assert.Equal(WatchRootState.Paused, disabledSnapshot.State);
        Assert.Null(disabledSnapshot.LatestPreview);
        Assert.Equal(WatchRootState.Synchronized, RootSnapshot(afterStart, FirstRootId).State);
        Assert.Equal(
            WatchRootPreviewOutcome.Ready,
            RootSnapshot(afterStart, FirstRootId).LatestPreview!.Outcome);
        Assert.Equal(WatchRootState.Synchronized, RootSnapshot(afterStart, ThirdRootId).State);

        Assert.Equal(0, beforeStart.Revision);
        Assert.All(beforeStart.Roots, static root => Assert.Null(root.LatestPreview));
        Assert.Equal(
            WatchRootState.RequiresInitialScan,
            RootSnapshot(beforeStart, FirstRootId).State);
        IList<WatchPreviewRootSnapshot> immutableRoots =
            Assert.IsAssignableFrom<IList<WatchPreviewRootSnapshot>>(afterStart.Roots);
        Assert.Throws<NotSupportedException>(() => immutableRoots.Clear());
        Assert.False(harness.Runtime.ResumeRoot(SecondRootId));
    }

    [Fact]
    public async Task OneRootFailureDoesNotBlockAnotherAndRetryOnlyRescansItsTarget()
    {
        WatchRootConfiguration first = Root(FirstRootId, @"C:\Watch-A");
        WatchRootConfiguration second = Root(SecondRootId, @"C:\Watch-B");
        int firstAttempts = 0;
        await using Harness harness = new([first, second]);
        harness.Scanner.Handler = (root, _, _) =>
        {
            if (Paths.AreEquivalent(root, first.RootPath)
                && Interlocked.Increment(ref firstAttempts) == 1)
            {
                throw new IOException("scripted root-local failure");
            }

            return CompleteScan(root, File(root, "same.txt", root.Value, 12));
        };

        harness.Runtime.Start();
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);
        WatchPreviewRuntimeSnapshot failed = harness.Runtime.Snapshot;
        WatchPreviewRootSnapshot failedFirst = RootSnapshot(failed, FirstRootId);
        WatchPreviewRootSnapshot readySecond = RootSnapshot(failed, SecondRootId);

        Assert.Equal(WatchRootState.RequiresInitialScan, failedFirst.State);
        Assert.Equal(WatchRootPreviewOutcome.Failed, failedFirst.LatestPreview!.Outcome);
        Assert.Equal(
            WatchRootPreviewProblemKind.SourceScanFailed,
            Assert.Single(failedFirst.LatestPreview.Problems).Kind);
        Assert.Equal(WatchRootState.Synchronized, readySecond.State);
        Assert.Equal(WatchRootPreviewOutcome.Ready, readySecond.LatestPreview!.Outcome);
        long secondRevision = readySecond.Revision;
        WatchRootPreviewResult secondPreview = readySecond.LatestPreview;
        Assert.False(harness.Runtime.RequestBaselineScan(SecondRootId));

        Assert.True(harness.Runtime.RequestBaselineScan(FirstRootId));
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);
        WatchPreviewRuntimeSnapshot retried = harness.Runtime.Snapshot;
        WatchPreviewRootSnapshot retriedFirst = RootSnapshot(retried, FirstRootId);
        WatchPreviewRootSnapshot unchangedSecond = RootSnapshot(retried, SecondRootId);

        Assert.Equal(WatchRootState.Synchronized, retriedFirst.State);
        Assert.Equal(WatchRootPreviewOutcome.Ready, retriedFirst.LatestPreview!.Outcome);
        Assert.Equal(2, harness.Scanner.CallCount(FirstRootId));
        Assert.Equal(1, harness.Scanner.CallCount(SecondRootId));
        Assert.Equal(secondRevision, unchangedSecond.Revision);
        Assert.Same(secondPreview, unchangedSecond.LatestPreview);

        OrganizationPlanItem firstItem = Assert.Single(retriedFirst.LatestPreview.Plan!.Items);
        OrganizationPlanItem secondItem = Assert.Single(unchangedSecond.LatestPreview!.Plan!.Items);
        Assert.Equal(PlanItemOutcome.Ready, firstItem.Outcome);
        Assert.Equal(PlanItemOutcome.Ready, secondItem.Outcome);
        Assert.Equal(firstItem.ProposedTargetPath!.Value, secondItem.ProposedTargetPath!.Value);
    }

    [Fact]
    public async Task FullRootScansRespectTheConfiguredGlobalConcurrencyLimit()
    {
        WatchRootConfiguration[] roots =
        [
            Root(FirstRootId, @"C:\Watch-A"),
            Root(SecondRootId, @"C:\Watch-B"),
            Root(ThirdRootId, @"C:\Watch-C"),
            Root(FourthRootId, @"C:\Watch-D"),
        ];
        await using Harness harness = new(roots, maxConcurrentScans: 2);
        TaskCompletionSource twoEntered = NewSignal();
        TaskCompletionSource release = NewSignal();
        int entered = 0;
        int active = 0;
        int maximum = 0;
        harness.Scanner.Handler = (root, _, cancellationToken) =>
        {
            int current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximum, current);
            if (Interlocked.Increment(ref entered) == 2)
            {
                twoEntered.TrySetResult();
            }

            try
            {
                release.Task.Wait(cancellationToken);
                return CompleteScan(root);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        };

        harness.Runtime.Start();
        await twoEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref entered));
        Assert.Equal(2, Volatile.Read(ref active));
        Assert.Equal(2, Volatile.Read(ref maximum));

        release.TrySetResult();
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, Volatile.Read(ref entered));
        Assert.Equal(0, Volatile.Read(ref active));
        Assert.Equal(2, Volatile.Read(ref maximum));
        Assert.All(
            harness.Runtime.Snapshot.Roots,
            static root =>
            {
                Assert.Equal(WatchRootState.Synchronized, root.State);
                Assert.Equal(WatchRootPreviewOutcome.Ready, root.LatestPreview!.Outcome);
            });
    }

    [Fact]
    public async Task PauseAndResumeAffectOnlyTheNamedRoot()
    {
        await using Harness harness = new(
        [
            Root(FirstRootId, @"C:\Watch-A"),
            Root(SecondRootId, @"C:\Watch-B"),
        ]);
        harness.Runtime.Start();
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);
        WatchPreviewRootSnapshot originalSecond =
            RootSnapshot(harness.Runtime.Snapshot, SecondRootId);

        Assert.True(harness.Runtime.PauseRoot(FirstRootId));
        Assert.False(harness.Runtime.PauseRoot(FirstRootId));
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);

        WatchPreviewRuntimeSnapshot paused = harness.Runtime.Snapshot;
        Assert.Equal(WatchRootState.Paused, RootSnapshot(paused, FirstRootId).State);
        Assert.Equal(originalSecond.Revision, RootSnapshot(paused, SecondRootId).Revision);
        Assert.Equal(1, harness.Scanner.CallCount(FirstRootId));
        Assert.Equal(1, harness.Scanner.CallCount(SecondRootId));

        Assert.True(harness.Runtime.ResumeRoot(FirstRootId));
        Assert.False(harness.Runtime.ResumeRoot(SecondRootId));
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);

        WatchPreviewRuntimeSnapshot resumed = harness.Runtime.Snapshot;
        Assert.Equal(WatchRootState.Synchronized, RootSnapshot(resumed, FirstRootId).State);
        Assert.Equal(originalSecond.Revision, RootSnapshot(resumed, SecondRootId).Revision);
        Assert.Equal(2, harness.Scanner.CallCount(FirstRootId));
        Assert.Equal(1, harness.Scanner.CallCount(SecondRootId));
        Assert.False(harness.Runtime.PauseRoot(Guid.Empty));
        Assert.False(harness.Runtime.ResumeRoot(Guid.NewGuid()));
        Assert.False(harness.Runtime.RequestBaselineScan(Guid.NewGuid()));
    }

    [Fact]
    public async Task ReconnectFaultedRootUsesFreshSourceAndRequiresBaselineBeforeTrust()
    {
        await using Harness harness = new([Root(FirstRootId, @"C:\Watch-A")]);
        harness.Runtime.Start();
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);
        ScriptedSource original = harness.SourceFactory.CurrentSource(FirstRootId);

        original.Raise(
            new FileChangeSourceEvent(
                FileChangeSourceEventKind.WatcherError,
                sequence: 1,
                problem: new FileChangeSourceProblem(
                    FileChangeSourceProblemKind.InputOutputFailure,
                    "scripted source failure")));
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            WatchRootState.Faulted,
            RootSnapshot(harness.Runtime.Snapshot, FirstRootId).State);
        Assert.Equal(1, original.DisposeCount);

        TaskCompletionSource scanEntered = NewSignal();
        TaskCompletionSource releaseScan = NewSignal();
        harness.Scanner.Handler = (root, _, cancellationToken) =>
        {
            scanEntered.TrySetResult();
            releaseScan.Task.Wait(cancellationToken);
            return CompleteScan(root);
        };

        Assert.True(harness.Runtime.ReconnectRoot(FirstRootId));
        try
        {
            await scanEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            WatchPreviewRootSnapshot awaitingBaseline =
                RootSnapshot(harness.Runtime.Snapshot, FirstRootId);

            Assert.Equal(WatchRootState.RescanRequired, awaitingBaseline.State);
            Assert.Equal(2, harness.SourceFactory.Requests.Length);
            Assert.Equal(2, harness.Scanner.CallCount(FirstRootId));
            Assert.NotSame(original, harness.SourceFactory.CurrentSource(FirstRootId));
        }
        finally
        {
            releaseScan.TrySetResult();
        }

        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            WatchRootState.Synchronized,
            RootSnapshot(harness.Runtime.Snapshot, FirstRootId).State);
    }

    [Fact]
    public async Task ReconnectOpenFailureRemainsUnavailableWithoutStartingBaseline()
    {
        await using Harness harness = new([Root(FirstRootId, @"C:\Watch-A")]);
        harness.Runtime.Start();
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);
        ScriptedSource original = harness.SourceFactory.CurrentSource(FirstRootId);

        original.Raise(
            new FileChangeSourceEvent(
                FileChangeSourceEventKind.WatcherError,
                sequence: 1,
                problem: new FileChangeSourceProblem(
                    FileChangeSourceProblemKind.Unavailable,
                    "scripted unavailable source")));
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            WatchRootState.Unavailable,
            RootSnapshot(harness.Runtime.Snapshot, FirstRootId).State);

        harness.SourceFactory.OpenHandler = static _ =>
            FileChangeSourceOpenResult.Unavailable(
                new FileChangeSourceProblem(
                    FileChangeSourceProblemKind.Inaccessible,
                    "scripted reopen denied"));

        Assert.True(harness.Runtime.ReconnectRoot(FirstRootId));
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);
        WatchPreviewRootSnapshot reopened =
            RootSnapshot(harness.Runtime.Snapshot, FirstRootId);

        Assert.Equal(WatchRootState.Unavailable, reopened.State);
        Assert.Equal("scripted reopen denied", reopened.LatestDiagnostic!.Description);
        Assert.Equal(2, harness.SourceFactory.Requests.Length);
        Assert.Equal(1, harness.Scanner.CallCount(FirstRootId));
        Assert.False(harness.Runtime.RequestBaselineScan(FirstRootId));
    }

    [Fact]
    public async Task ReconnectRejectsNonFailureStatesWithoutReopeningSource()
    {
        await using Harness harness = new(
        [
            Root(FirstRootId, @"C:\Watch-A"),
            Root(SecondRootId, @"C:\Watch-B", isEnabled: false),
        ]);
        TaskCompletionSource initialScanEntered = NewSignal();
        TaskCompletionSource releaseInitialScan = NewSignal();
        TaskCompletionSource resumedScanEntered = NewSignal();
        TaskCompletionSource releaseResumedScan = NewSignal();
        int attempts = 0;
        harness.Scanner.Handler = (root, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                initialScanEntered.TrySetResult();
                releaseInitialScan.Task.Wait(cancellationToken);
            }
            else
            {
                resumedScanEntered.TrySetResult();
                releaseResumedScan.Task.Wait(cancellationToken);
            }

            return CompleteScan(root);
        };

        Assert.False(harness.Runtime.ReconnectRoot(FirstRootId));
        Assert.False(harness.Runtime.ReconnectRoot(SecondRootId));
        Assert.False(harness.Runtime.ReconnectRoot(Guid.NewGuid()));
        Assert.Empty(harness.SourceFactory.Requests);

        harness.Runtime.Start();
        await initialScanEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(
                WatchRootState.RequiresInitialScan,
                RootSnapshot(harness.Runtime.Snapshot, FirstRootId).State);
            Assert.False(harness.Runtime.ReconnectRoot(FirstRootId));
            Assert.Single(harness.SourceFactory.Requests);
        }
        finally
        {
            releaseInitialScan.TrySetResult();
        }

        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            WatchRootState.Synchronized,
            RootSnapshot(harness.Runtime.Snapshot, FirstRootId).State);
        Assert.False(harness.Runtime.ReconnectRoot(FirstRootId));
        Assert.Single(harness.SourceFactory.Requests);

        Assert.True(harness.Runtime.PauseRoot(FirstRootId));
        Assert.Equal(
            WatchRootState.Paused,
            RootSnapshot(harness.Runtime.Snapshot, FirstRootId).State);
        Assert.False(harness.Runtime.ReconnectRoot(FirstRootId));
        Assert.Single(harness.SourceFactory.Requests);

        Assert.True(harness.Runtime.ResumeRoot(FirstRootId));
        await resumedScanEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(
                WatchRootState.RescanRequired,
                RootSnapshot(harness.Runtime.Snapshot, FirstRootId).State);
            Assert.False(harness.Runtime.ReconnectRoot(FirstRootId));
            Assert.Equal(2, harness.SourceFactory.Requests.Length);
        }
        finally
        {
            releaseResumedScan.TrySetResult();
        }

        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BlockedSubscriberDoesNotBlockWatcherCallbackAndPendingNotificationUnionsChangedRoots()
    {
        await using Harness harness = new(
        [
            Root(FirstRootId, @"C:\Watch-A"),
            Root(SecondRootId, @"C:\Watch-B"),
            Root(ThirdRootId, @"C:\Watch-C"),
        ]);
        harness.Runtime.Start();
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);

        TaskCompletionSource subscriberEntered = NewSignal();
        TaskCompletionSource releaseSubscriber = NewSignal();
        ConcurrentQueue<WatchPreviewRuntimeNotification> notifications = new();
        int calls = 0;
        harness.Runtime.SnapshotChanged += (_, notification) =>
        {
            notifications.Enqueue(notification);
            if (Interlocked.Increment(ref calls) == 1)
            {
                subscriberEntered.TrySetResult();
                releaseSubscriber.Task.GetAwaiter().GetResult();
            }
        };

        Assert.True(harness.Runtime.PauseRoot(FirstRootId));
        await subscriberEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        ScriptedSource secondSource = harness.SourceFactory.CurrentSource(SecondRootId);
        Task callback = Task.Run(
            () =>
            {
                secondSource.Raise(
                    new FileChangeSourceEvent(
                        FileChangeSourceEventKind.WatcherError,
                        sequence: 1,
                        problem: new FileChangeSourceProblem(
                            FileChangeSourceProblemKind.Inaccessible,
                            "scripted inaccessible source")));
                Assert.True(harness.Runtime.PauseRoot(ThirdRootId));
            },
            TestContext.Current.CancellationToken);
        await callback.WaitAsync(TestContext.Current.CancellationToken);
        Task drain = harness.Runtime.DrainAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(drain.IsCompleted);

        releaseSubscriber.TrySetResult();
        await drain;

        WatchPreviewRuntimeNotification[] delivered = [.. notifications];
        Assert.Equal(2, delivered.Length);
        WatchPreviewRuntimeNotification coalesced = Assert.Single(
            delivered,
            static notification => notification.ChangedRootIds.Count == 2);
        Assert.Equal([SecondRootId, ThirdRootId], coalesced.ChangedRootIds);
        Assert.Equal(
            [FirstRootId, SecondRootId, ThirdRootId],
            coalesced.Snapshot.Roots.Select(static root => root.WatchRootId));
    }

    [Fact]
    public async Task ConcurrentDisposeJoinsBlockedScanAndRejectsLateSourceCallbacks()
    {
        Harness harness = new([Root(FirstRootId, @"C:\Watch-A")]);
        TaskCompletionSource scanEntered = NewSignal();
        TaskCompletionSource releaseScan = NewSignal();
        harness.Scanner.Handler = (root, _, _) =>
        {
            scanEntered.TrySetResult();
            releaseScan.Task.GetAwaiter().GetResult();
            return CompleteScan(root);
        };
        int notifications = 0;
        harness.Runtime.SnapshotChanged += (_, _) => Interlocked.Increment(ref notifications);
        harness.Runtime.Start();
        await scanEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        ScriptedSource source = harness.SourceFactory.CurrentSource(FirstRootId);

        Task first = harness.Runtime.DisposeAsync().AsTask();
        Task second = harness.Runtime.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        releaseScan.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TestContext.Current.CancellationToken);
        WatchPreviewRuntimeSnapshot disposedSnapshot = harness.Runtime.Snapshot;
        int notificationCount = Volatile.Read(ref notifications);

        source.RaiseLate(
            new FileChangeSourceEvent(
                FileChangeSourceEventKind.Overflow,
                sequence: 1,
                problem: new FileChangeSourceProblem(
                    FileChangeSourceProblemKind.BufferOverflow,
                    "late scripted overflow")));
        await harness.Runtime.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(disposedSnapshot.Revision, harness.Runtime.Snapshot.Revision);
        Assert.Equal(notificationCount, Volatile.Read(ref notifications));
        Assert.False(harness.Runtime.PauseRoot(FirstRootId));
        Assert.False(harness.Runtime.ReconnectRoot(FirstRootId));
        Assert.Equal(1, source.DisposeCount);
    }

    private static void UpdateMaximum(ref int maximum, int current)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (current <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maximum, current, observed) != observed);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static WatchRootConfiguration Root(
        Guid id,
        string path,
        bool isEnabled = true,
        bool includeSubdirectories = true) =>
        new(id, Canonical(path), isEnabled, includeSubdirectories);

    private static WatchPreviewRootSnapshot RootSnapshot(
        WatchPreviewRuntimeSnapshot snapshot,
        Guid rootId) =>
        Assert.Single(snapshot.Roots, root => root.WatchRootId == rootId);

    private static CanonicalPath Canonical(string path) =>
        Paths.CanonicalizeAbsolute(path).Path
        ?? throw new InvalidOperationException($"Invalid test path: {path}");

    private static FileScanResult CompleteScan(
        CanonicalPath root,
        params FileEntry[] files) =>
        new(root, files, [], RootIdentity(root.Value));

    private static FileEntry File(
        CanonicalPath root,
        string name,
        string identity,
        long length) =>
        new(
            Canonical(root.Value + @"\" + name),
            length,
            Now.AddDays(-2),
            Now.AddMinutes(-1),
            FileIdentity(identity));

    private static PhysicalEntryIdentity FileIdentity(string identity) =>
        new("test", 1, "volume", identity, PhysicalEntryKind.RegularFile);

    private static PhysicalEntryIdentity RootIdentity(string identity) =>
        new("test", 1, "volume", identity, PhysicalEntryKind.Directory);

    private static OrganizationConfiguration Configuration(
        IEnumerable<WatchRootConfiguration> roots)
    {
        OrganizationRule rule = new(
            new Rule("all files", AlwaysMatchCondition.Instance),
            new MoveToDirectoryAction(@"C:\Destination"));
        return new OrganizationConfiguration(
            Paths.Semantics,
            [new ConfiguredOrganizationRule(RuleId, rule)],
            roots);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            IEnumerable<WatchRootConfiguration> roots,
            int maxConcurrentScans = 2)
        {
            WatchRootConfiguration[] rootSnapshot = [.. roots];
            SourceFactory = new ScriptedSourceFactory();
            Scanner = new ScriptedScanner();
            Runtime = new WatchPreviewRuntime(
                Configuration(rootSnapshot),
                SourceFactory,
                new MissingObservationPort(),
                Scanner,
                new ScriptedDestinationInspector(),
                new OrganizationPlanner(new FixedTimeProvider(Now)),
                new ImmediateDelay(),
                new FixedTimeProvider(Now),
                new WatchPreviewRuntimeOptions(
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(1),
                    maxConcurrentScans));
        }

        public WatchPreviewRuntime Runtime { get; }

        public ScriptedSourceFactory SourceFactory { get; }

        public ScriptedScanner Scanner { get; }

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class ScriptedScanner : IFileScanner
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, int> _callCounts = [];
        private readonly Dictionary<Guid, List<bool>> _recursiveArguments = [];
        private readonly Dictionary<string, Guid> _rootIds = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Watch-A"] = FirstRootId,
            [@"C:\Watch-B"] = SecondRootId,
            [@"C:\Watch-C"] = ThirdRootId,
            [@"C:\Watch-D"] = FourthRootId,
        };

        public Func<CanonicalPath, bool, CancellationToken, FileScanResult>? Handler { get; set; }

        public int CallCount(Guid rootId)
        {
            lock (_gate)
            {
                return _callCounts.GetValueOrDefault(rootId);
            }
        }

        public bool[] RecursiveArguments(Guid rootId)
        {
            lock (_gate)
            {
                return _recursiveArguments.TryGetValue(rootId, out List<bool>? arguments)
                    ? arguments.ToArray()
                    : [];
            }
        }

        public FileScanResult Scan(
            CanonicalPath sourceRoot,
            bool recursive,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Guid rootId = _rootIds[sourceRoot.Value];
                _callCounts[rootId] = _callCounts.GetValueOrDefault(rootId) + 1;
                if (!_recursiveArguments.TryGetValue(rootId, out List<bool>? arguments))
                {
                    arguments = [];
                    _recursiveArguments.Add(rootId, arguments);
                }

                arguments.Add(recursive);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Handler?.Invoke(sourceRoot, recursive, cancellationToken)
                ?? CompleteScan(sourceRoot);
        }
    }

    private sealed class ScriptedDestinationInspector : IDestinationStateInspector
    {
        public DestinationStateSnapshot Inspect(
            OrganizationPlanPreparation preparation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathPolicy policy = new(
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
        private readonly object _gate = new();
        private readonly List<FileChangeSourceRequest> _requests = [];
        private readonly Dictionary<Guid, ScriptedSource> _currentSources = [];

        public Func<FileChangeSourceRequest, FileChangeSourceOpenResult>? OpenHandler { get; set; }

        public FileChangeSourceRequest[] Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        public FileChangeSourceOpenResult Open(FileChangeSourceRequest request)
        {
            FileChangeSourceOpenResult result = OpenHandler?.Invoke(request)
                ?? FileChangeSourceOpenResult.Opened(new ScriptedSource());
            lock (_gate)
            {
                _requests.Add(request);
                if (result.Source is ScriptedSource source)
                {
                    _currentSources[request.WatchRootId] = source;
                }
            }

            return result;
        }

        public ScriptedSource CurrentSource(Guid rootId)
        {
            lock (_gate)
            {
                return _currentSources[rootId];
            }
        }
    }

    private sealed class ScriptedSource : IFileChangeSource
    {
        private readonly object _gate = new();
        private readonly List<EventHandler<FileChangeSourceEvent>> _historicalHandlers = [];
        private EventHandler<FileChangeSourceEvent>? _handlers;

        public event EventHandler<FileChangeSourceEvent>? EventReceived
        {
            add
            {
                lock (_gate)
                {
                    _handlers += value;
                    _historicalHandlers.Add(value!);
                }
            }

            remove
            {
                lock (_gate)
                {
                    _handlers -= value;
                }
            }
        }

        public int DisposeCount { get; private set; }

        public void Start()
        {
        }

        public void RaiseLate(FileChangeSourceEvent change)
        {
            EventHandler<FileChangeSourceEvent>[] handlers;
            lock (_gate)
            {
                handlers = [.. _historicalHandlers];
            }

            foreach (EventHandler<FileChangeSourceEvent> handler in handlers)
            {
                handler(this, change);
            }
        }

        public void Raise(FileChangeSourceEvent change)
        {
            EventHandler<FileChangeSourceEvent>? handlers;
            lock (_gate)
            {
                handlers = _handlers;
            }

            handlers?.Invoke(this, change);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                DisposeCount++;
                _handlers = null;
            }
        }
    }

    private sealed class MissingObservationPort : IFileStabilityObservationPort
    {
        public ValueTask<PhysicalEntryObservation> ObserveAsync(
            CanonicalPath path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(PhysicalEntryObservation.Missing());
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
