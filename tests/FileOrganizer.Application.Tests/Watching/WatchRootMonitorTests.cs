using System.Collections.Concurrent;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Application.Watching;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Tests.Watching;

public sealed class WatchRootMonitorTests
{
    private static readonly Guid RootId =
        Guid.Parse("11111111-aaaa-4444-8888-111111111111");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartupRequiresUnchangedBaselineTokenBeforeSynchronization()
    {
        await using Harness harness = new();
        Assert.Equal(WatchRootState.RequiresInitialScan, harness.Monitor.State);

        harness.Monitor.Start();
        WatchBaselineToken stale = harness.Monitor.CaptureBaselineToken();
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, Harness.File("a.txt")));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.False(harness.Monitor.AcknowledgeBaseline(stale));
        Assert.Equal(WatchRootState.RescanRequired, harness.Monitor.State);
        Assert.Empty(harness.Observations.Paths);
        Assert.Empty(harness.Delay.Active);

        WatchBaselineToken current = harness.Monitor.CaptureBaselineToken();
        Assert.True(harness.Monitor.AcknowledgeBaseline(current));
        Assert.Equal(WatchRootState.Synchronized, harness.Monitor.State);
        Assert.All(harness.Notifications, notification => Assert.Equal(RootId, notification.WatchRootId));
        Assert.Contains(
            harness.Notifications,
            notification => notification.Diagnostic?.Kind == WatchDiagnosticKind.BaselineMismatch);
    }

    [Fact]
    public async Task BaselineRequiredNotificationIsNotPublishedBeforePhysicalSourceStarts()
    {
        await using Harness harness = new(subscribe: false);
        bool sourceWasStarted = false;
        harness.Monitor.Notification += (_, notification) =>
        {
            if (notification.State == WatchRootState.RequiresInitialScan)
            {
                sourceWasStarted = harness.Factory.Sources[0].IsStarted;
            }
        };

        harness.Monitor.Start();

        Assert.True(sourceWasStarted);
        Assert.True(harness.Monitor.AcknowledgeBaseline(harness.Monitor.CaptureBaselineToken()));
    }

    [Fact]
    public async Task CreatedChangedBurstCollapsesAndEmitsOneStableVersion()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string path = Harness.File("report.txt");
        harness.Observations.Enqueue(path, File("same", 42), File("same", 42));

        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, path));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Changed, 2, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Single(harness.Delay.Active);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        StableFileCandidate candidate = Assert.Single(harness.Candidates);
        Assert.Equal(RootId, candidate.WatchRootId);
        Assert.Equal(path, candidate.Path.Value);
        Assert.Equal(2, candidate.Generation);
        Assert.Equal(Now, candidate.ObservedAtUtc);
        Assert.Equal(2, harness.Observations.Paths.Count);

        harness.Observations.Enqueue(path, File("same", 42), File("same", 42));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Changed, 3, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        Assert.Single(harness.Candidates);
    }

    [Fact]
    public async Task IndependentFilesDebounceAndStabilizeIndependently()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string first = Harness.File("first.txt");
        string second = Harness.File("second.txt");
        harness.Observations.Enqueue(first, File("first", 1), File("first", 1));
        harness.Observations.Enqueue(second, File("second", 2), File("second", 2));

        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, first));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 2, second));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, harness.Delay.Active.Length);
        for (int index = 0; index < 4; index++)
        {
            await harness.Delay.ReleaseNextAsync(harness.Monitor);
        }

        Assert.Equal(
            [first, second],
            harness.Candidates.Select(static candidate => candidate.Path.Value).Order().ToArray());
    }

    [Fact]
    public async Task UnprovenSiblingDoesNotDelayAnIndependentStableFile()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string fast = Harness.File("fast.txt");
        string slow = Harness.File("slow.txt");
        harness.Observations.Enqueue(fast, File("fast", 1), File("fast", 1));

        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, fast));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 2, slow));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        await harness.Delay.ReleaseDurationAsync(
            TimeSpan.FromSeconds(3),
            harness.Monitor);

        StableFileCandidate candidate = Assert.Single(harness.Candidates);
        Assert.Equal(fast, candidate.Path.Value);
        Assert.Contains(TimeSpan.FromSeconds(2), harness.Delay.Active);
        Assert.DoesNotContain(slow, harness.Observations.Paths);
    }

    [Fact]
    public async Task BlockingPhysicalObservationDoesNotBlockUnrelatedEventClassification()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string heldPath = Harness.File("held-observation.txt");
        string otherPath = Harness.File("other.txt");
        harness.Observations.Enqueue(heldPath);
        HeldObservation held = harness.Observations.EnqueueHeld(heldPath);

        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, heldPath));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        harness.Delay.ReleaseNext();
        await held.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 2, otherPath));
        await harness.Delay.WaitForActiveCountAsync(
            1,
            TestContext.Current.CancellationToken);

        Assert.Contains(TimeSpan.FromSeconds(2), harness.Delay.Active);
        held.Completion.TrySetResult(File("held", 1));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MetadataAndIdentityDriftRestartStabilityUntilOneVersionSettles()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string path = Harness.File("download.bin");
        harness.Observations.Enqueue(
            path,
            File("first", 10),
            File("first", 20),
            File("second", 20),
            File("second", 20));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        Assert.Empty(harness.Candidates);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        Assert.Empty(harness.Candidates);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        StableFileCandidate candidate = Assert.Single(harness.Candidates);
        Assert.Equal("second", candidate.Identity.EntryIdentifier);
        Assert.Equal(20, candidate.Metadata.Length);
        Assert.Equal(
            2,
            harness.Notifications.Count(
                notification => notification.Diagnostic?.Kind == WatchDiagnosticKind.Unstable));
    }

    [Fact]
    public async Task ChangeDuringStabilityCancelsOldProofAndRestartsFromDebounce()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string path = Harness.File("active-write.bin");
        harness.Observations.Enqueue(
            path,
            File("before", 10),
            File("after", 20),
            File("after", 20));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        harness.Source.Raise(Change(FileChangeSourceEventKind.Changed, 2, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Single(harness.Delay.Active);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        StableFileCandidate candidate = Assert.Single(harness.Candidates);
        Assert.Equal("after", candidate.Identity.EntryIdentifier);
        Assert.Equal(2, candidate.Generation);
        Assert.Equal(3, harness.Observations.Paths.Count);
    }

    [Fact]
    public async Task ChangeArrivingAtCandidatePublicationInvalidatesTheOldProof()
    {
        CallbackTimeProvider timeProvider = new(Now);
        await using Harness harness = new(timeProvider: timeProvider);
        await harness.SynchronizeAsync();
        string path = Harness.File("publication-race.txt");
        harness.Observations.Enqueue(
            path,
            File("before", 10),
            File("before", 10),
            File("after", 20),
            File("after", 20));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        timeProvider.InvokeOnce(
            () => harness.Source.Raise(
                Change(FileChangeSourceEventKind.Changed, 2, path)));
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        Assert.Empty(harness.Candidates);
        Assert.Single(harness.Delay.Active);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        StableFileCandidate candidate = Assert.Single(harness.Candidates);
        Assert.Equal("after", candidate.Identity.EntryIdentifier);
        Assert.Equal(2, candidate.Generation);
    }

    [Fact]
    public async Task QueuedOldSessionEventCannotAffectAnAcknowledgedReplacementSource()
    {
        BlockingTimeProvider timeProvider = new(Now);
        await using Harness harness = new(timeProvider: timeProvider);
        await harness.SynchronizeAsync();
        ScriptedSource original = harness.Source;
        string proving = Harness.File("proving.txt");
        string stale = Harness.File("stale.txt");
        harness.Observations.Enqueue(proving, File("same", 1), File("same", 1));
        original.Raise(Change(FileChangeSourceEventKind.Created, 1, proving));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        harness.Delay.ReleaseNext();
        await timeProvider.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        original.Raise(Change(FileChangeSourceEventKind.Created, 2, stale));
        harness.Monitor.Pause();
        Assert.True(harness.Monitor.Resume());
        WatchBaselineToken token = harness.Monitor.CaptureBaselineToken();
        Assert.True(harness.Monitor.AcknowledgeBaseline(token));
        timeProvider.Release();
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Empty(harness.Candidates);
        Assert.DoesNotContain(stale, harness.Observations.Paths);
        Assert.Empty(harness.Delay.Active);
    }

    [Fact]
    public async Task DeleteAndRecreateNeverReusesCandidateGeneration()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string path = Harness.File("recreated.txt");
        harness.Observations.Enqueue(path, File("first", 1), File("first", 1));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        harness.Source.Raise(Change(FileChangeSourceEventKind.Deleted, 2, path));
        harness.Observations.Enqueue(path, File("second", 2), File("second", 2));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 3, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        Assert.Equal([1L, 2L], harness.Candidates.Select(static candidate => candidate.Generation));
    }

    [Fact]
    public async Task DeleteCancelsPendingCandidate()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string path = Harness.File("gone.txt");
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        harness.Source.Raise(Change(FileChangeSourceEventKind.Deleted, 2, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Empty(harness.Delay.Active);
        Assert.Empty(harness.Observations.Paths);
        Assert.Empty(harness.Candidates);
    }

    [Fact]
    public async Task RenameWithinRootFollowsNewPathAndRestartsStability()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string oldPath = Harness.File("old.txt");
        string newPath = Harness.File("new.txt");
        harness.Observations.Enqueue(oldPath, File("same", 3));
        harness.Observations.Enqueue(newPath, File("same", 3), File("same", 3));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, oldPath));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        harness.Source.Raise(Rename(2, oldPath, newPath));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        Assert.Equal(newPath, Assert.Single(harness.Candidates).Path.Value);
        Assert.Equal([oldPath, newPath, newPath], harness.Observations.Paths);
    }

    [Fact]
    public async Task RenameIntoIsNewCandidateAndKnownRenameOutCancelsPending()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string into = Harness.File("into.txt");
        string outside = @"D:\Outside\file.txt";
        harness.Observations.Enqueue(into, File("into", 7), File("into", 7));
        harness.Source.Raise(Rename(1, outside, into));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        Assert.Equal(into, Assert.Single(harness.Candidates).Path.Value);

        string pending = Harness.File("pending.txt");
        harness.Observations.Enqueue(pending, File("pending", 1));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 2, pending));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        harness.Source.Raise(Rename(3, pending, outside));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WatchRootState.Synchronized, harness.Monitor.State);
        Assert.Empty(harness.Delay.Active);
    }

    [Fact]
    public async Task UnobservedPendingEntryRenamedOutRequiresRescan()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string pending = Harness.File("unknown-kind");
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, pending));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        harness.Source.Raise(Rename(2, pending, @"D:\Outside\unknown-kind"));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WatchRootState.RescanRequired, harness.Monitor.State);
        Assert.Empty(harness.Candidates);
    }

    [Fact]
    public async Task DirectoryObservationRequiresRescanInsteadOfGuessingScope()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string path = Harness.File("folder");
        harness.Observations.Enqueue(path, Directory("directory"));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        Assert.Equal(WatchRootState.RescanRequired, harness.Monitor.State);
        Assert.Contains(
            harness.Notifications,
            notification => notification.Diagnostic?.Kind
                == WatchDiagnosticKind.DirectoryScopeChanged);
        Assert.Empty(harness.Candidates);
    }

    [Theory]
    [InlineData(0, WatchDiagnosticKind.ReparsePoint)]
    [InlineData(1, WatchDiagnosticKind.UnsupportedEntry)]
    [InlineData(2, WatchDiagnosticKind.Inaccessible)]
    [InlineData(3, WatchDiagnosticKind.Missing)]
    [InlineData(4, WatchDiagnosticKind.UnsupportedCapability)]
    public async Task UnsafeUnprovenObservationRequiresRescanWithStructuredDiagnostic(
        int scenario,
        WatchDiagnosticKind expected)
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string path = Harness.File("unsafe.txt");
        PhysicalEntryObservation observation = scenario switch
        {
            0 => PhysicalEntryObservation.Unsupported(
                PhysicalEntryObservationReason.ReparsePoint,
                PhysicalEntryKind.ReparsePoint),
            1 => PhysicalEntryObservation.Unsupported(
                PhysicalEntryObservationReason.UnsupportedEntryKind,
                PhysicalEntryKind.Unsupported),
            2 => PhysicalEntryObservation.Inaccessible(
                PhysicalEntryObservationReason.AccessDenied,
                nativeErrorCode: 5),
            3 => PhysicalEntryObservation.Missing(),
            _ => PhysicalEntryObservation.Unsupported(
                PhysicalEntryObservationReason.UnsupportedCapability),
        };
        harness.Observations.Enqueue(path, observation);
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        WatchRootNotification diagnostic = Assert.Single(
            harness.Notifications,
            notification => notification.Diagnostic?.Kind == expected);
        Assert.Equal(path, diagnostic.AffectedPath!.Value);
        Assert.DoesNotContain(path, diagnostic.Diagnostic!.Description, StringComparison.Ordinal);
        Assert.Empty(harness.Candidates);
        Assert.Equal(WatchRootState.RescanRequired, harness.Monitor.State);
    }

    [Fact]
    public async Task OverflowDiscardsPendingWorkAndReopensUntrustedSource()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        ScriptedSource original = harness.Source;
        original.Raise(
            Change(FileChangeSourceEventKind.Created, 1, Harness.File("pending.txt")));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        original.Raise(
            new FileChangeSourceEvent(
                FileChangeSourceEventKind.Overflow,
                2,
                problem: new FileChangeSourceProblem(
                    FileChangeSourceProblemKind.BufferOverflow,
                    "The native event buffer overflowed.")));

        Assert.Equal(WatchRootState.RescanRequired, harness.Monitor.State);
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.True(original.IsDisposed);
        Assert.Equal(WatchRootState.RescanRequired, harness.Monitor.State);
        Assert.Equal(2, harness.Factory.Sources.Count);
        ScriptedSource replacement = harness.Factory.Sources[1];
        Assert.True(replacement.IsStarted);
        replacement.Raise(Change(FileChangeSourceEventKind.Created, 1, Harness.File("ignored.txt")));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Empty(harness.Delay.Active);
    }

    [Fact]
    public async Task OverflowCleanupCannotSelfDeadlockAJoiningSource()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        ScriptedSource original = harness.Source;
        original.JoinCallbacksOnDispose = true;

        await Task.Run(
                () => original.Raise(
                    new FileChangeSourceEvent(
                        FileChangeSourceEventKind.Overflow,
                        1,
                        problem: new FileChangeSourceProblem(
                            FileChangeSourceProblemKind.BufferOverflow,
                            "The native event buffer overflowed."))),
                TestContext.Current.CancellationToken)
            .WaitAsync(TestContext.Current.CancellationToken);
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.True(original.IsDisposed);
        Assert.Equal(2, harness.Factory.Sources.Count);
    }

    [Theory]
    [InlineData(FileChangeSourceEventKind.Overflow, FileChangeSourceProblemKind.BufferOverflow)]
    [InlineData(FileChangeSourceEventKind.WatcherError, FileChangeSourceProblemKind.InputOutputFailure)]
    public async Task ReentrantDisposeStillCleansUpTheDetachedPhysicalSource(
        FileChangeSourceEventKind eventKind,
        FileChangeSourceProblemKind problemKind)
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        ScriptedSource original = harness.Source;
        harness.Monitor.Notification += (_, notification) =>
        {
            if (notification.Kind == WatchRootNotificationKind.Diagnostic)
            {
                harness.Monitor.Dispose();
            }
        };

        original.Raise(
            new FileChangeSourceEvent(
                eventKind,
                1,
                problem: new FileChangeSourceProblem(problemKind, "Structured source failure.")));
        await harness.Monitor.DisposeAsync();

        Assert.True(original.IsDisposed);
        Assert.False(original.HasSubscribers);
    }

    [Theory]
    [InlineData(FileChangeSourceProblemKind.InputOutputFailure, WatchRootState.Faulted)]
    [InlineData(FileChangeSourceProblemKind.Inaccessible, WatchRootState.Unavailable)]
    public async Task WatcherErrorDetachesSourceAndTransitionsToStructuredFailure(
        FileChangeSourceProblemKind problemKind,
        WatchRootState expectedState)
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        harness.Source.Raise(
            new FileChangeSourceEvent(
                FileChangeSourceEventKind.WatcherError,
                1,
                problem: new FileChangeSourceProblem(problemKind, "Structured source failure.")));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedState, harness.Monitor.State);
        Assert.True(harness.Source.IsDisposed);
        Assert.False(harness.Source.HasSubscribers);
        Assert.Contains(
            harness.Notifications,
            notification => notification.State == expectedState);
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 2, Harness.File("late.txt")));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Empty(harness.Candidates);
        Assert.Empty(harness.Delay.Active);
    }

    [Fact]
    public async Task UnknownRenameOutRequiresRescan()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        harness.Source.Raise(
            Rename(1, Harness.File("unknown"), @"D:\Outside\unknown"));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WatchRootState.RescanRequired, harness.Monitor.State);
        Assert.Contains(
            harness.Notifications,
            notification => notification.Diagnostic?.Kind == WatchDiagnosticKind.UnknownRename);
    }

    [Fact]
    public async Task PauseDisposesSourceAndResumeUsesFreshUnsynchronizedSource()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        ScriptedSource first = harness.Source;
        first.Raise(Change(FileChangeSourceEventKind.Created, 1, Harness.File("pending.txt")));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        Assert.Single(harness.Delay.Active);
        harness.Monitor.Pause();

        Assert.True(first.IsDisposed);
        Assert.Equal(WatchRootState.Paused, harness.Monitor.State);
        Assert.False(first.HasSubscribers);
        Assert.Empty(harness.Delay.Active);
        Assert.Empty(harness.Candidates);
        Assert.True(harness.Monitor.Resume());
        Assert.Equal(WatchRootState.RescanRequired, harness.Monitor.State);
        Assert.Equal(2, harness.Factory.Sources.Count);
        Assert.True(harness.Factory.Sources[1].IsStarted);

        WatchBaselineToken token = harness.Monitor.CaptureBaselineToken();
        Assert.True(harness.Monitor.AcknowledgeBaseline(token));
        Assert.Equal(WatchRootState.Synchronized, harness.Monitor.State);
    }

    [Fact]
    public async Task ReentrantResumeIsAcceptedOnceAndImmediatelyRequiresRescan()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        List<bool> resumeResults = [];
        EventHandler<WatchRootNotification> resume = (_, notification) =>
        {
            if (notification.State == WatchRootState.Paused)
            {
                resumeResults.Add(harness.Monitor.Resume());
            }
        };
        harness.Monitor.Notification += resume;
        harness.Monitor.Notification += resume;

        harness.Monitor.Pause();
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal([true, false], resumeResults);
        Assert.Equal(WatchRootState.RescanRequired, harness.Monitor.State);
        Assert.Equal(2, harness.Factory.Sources.Count);
        Assert.True(harness.Factory.Sources[1].IsStarted);
    }

    [Fact]
    public async Task ThrowingSubscriberCannotBreakOtherSubscribersOrStableCandidate()
    {
        await using Harness harness = new(subscribe: false);
        ConcurrentQueue<WatchRootNotification> received = new();
        harness.Monitor.Notification += static (_, _) => throw new InvalidOperationException();
        harness.Monitor.Notification += (_, notification) => received.Enqueue(notification);
        await harness.SynchronizeAsync();
        string path = Harness.File("safe.txt");
        harness.Observations.Enqueue(path, File("safe", 1), File("safe", 1));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        Assert.Contains(received, notification => notification.Candidate?.Path.Value == path);
    }

    [Fact]
    public async Task ReentrantPauseIsOrderedAfterCandidateForEverySubscriber()
    {
        await using Harness harness = new(subscribe: false);
        List<WatchRootNotificationKind> first = [];
        List<WatchRootNotificationKind> second = [];
        harness.Monitor.Notification += (_, notification) =>
        {
            first.Add(notification.Kind);
            if (notification.Kind == WatchRootNotificationKind.Candidate)
            {
                harness.Monitor.Pause();
            }
        };
        harness.Monitor.Notification += (_, notification) => second.Add(notification.Kind);
        await harness.SynchronizeAsync();
        first.Clear();
        second.Clear();
        string path = Harness.File("ordered.txt");
        harness.Observations.Enqueue(path, File("ordered", 1), File("ordered", 1));
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);

        Assert.Equal(
            [WatchRootNotificationKind.Candidate, WatchRootNotificationKind.StateChanged],
            first);
        Assert.Equal(first, second);
        Assert.Equal(WatchRootState.Paused, harness.Monitor.State);
    }

    [Fact]
    public async Task ConcurrentCallbacksCollapseSafelyAndDisposalCancelsPendingWork()
    {
        Harness harness = await Harness.SynchronizedAsync();
        string path = Harness.File("concurrent.txt");
        Parallel.For(
            1,
            101,
            sequence => harness.Source.Raise(
                Change(FileChangeSourceEventKind.Changed, sequence, path)));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Single(harness.Delay.Active);
        await harness.Monitor.DisposeAsync();
        Assert.True(harness.Source.IsDisposed);
        Assert.False(harness.Source.HasSubscribers);
        Assert.Empty(harness.Delay.Active);
        Assert.Empty(harness.Candidates);
    }

    [Fact]
    public async Task UniquePathBurstSaturatesBoundedQueueAndInvalidatesAllTrackedWork()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        ScriptedSource source = harness.Source;
        for (int index = 0; index < 700; index++)
        {
            source.Raise(
                Change(
                    FileChangeSourceEventKind.Created,
                    index + 1,
                    Harness.File($"burst-{index:D4}.txt")));
        }

        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WatchRootState.RescanRequired, harness.Monitor.State);
        Assert.Empty(harness.Candidates);
        Assert.Empty(harness.Delay.Active);
    }

    [Fact]
    public async Task OverflowWhileSecondObservationIsHeldCannotEmitCandidate()
    {
        await using Harness harness = await Harness.SynchronizedAsync();
        string path = Harness.File("held.txt");
        harness.Observations.Enqueue(path, File("held", 5));
        HeldObservation held = harness.Observations.EnqueueHeld(path);
        harness.Source.Raise(Change(FileChangeSourceEventKind.Created, 1, path));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);
        await harness.Delay.ReleaseNextAsync(harness.Monitor);
        harness.Delay.ReleaseNext();
        await held.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        TaskCompletionSource rescan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Monitor.Notification += (_, notification) =>
        {
            if (notification.State == WatchRootState.RescanRequired)
            {
                rescan.TrySetResult();
            }
        };
        ScriptedSource original = harness.Source;
        original.Raise(
            new FileChangeSourceEvent(
                FileChangeSourceEventKind.Overflow,
                2,
                problem: new FileChangeSourceProblem(
                    FileChangeSourceProblemKind.BufferOverflow,
                    "The native event buffer overflowed.")));
        await rescan.Task.WaitAsync(TestContext.Current.CancellationToken);

        held.Completion.TrySetResult(File("held", 5));
        await harness.Monitor.DrainAsync(TestContext.Current.CancellationToken);

        Assert.True(original.IsDisposed);
        Assert.Equal(2, harness.Factory.Sources.Count);
        Assert.Equal(WatchRootState.RescanRequired, harness.Monitor.State);
        Assert.Empty(harness.Candidates);
    }

    private static FileChangeSourceEvent Change(
        FileChangeSourceEventKind kind,
        long sequence,
        string path) => new(kind, sequence, path);

    private static FileChangeSourceEvent Rename(long sequence, string oldPath, string newPath) =>
        new(FileChangeSourceEventKind.Renamed, sequence, newPath, oldPath);

    private static PhysicalEntryObservation File(string id, long length) =>
        PhysicalEntryObservation.Success(
            new PhysicalEntryIdentity("test", 1, "volume", id, PhysicalEntryKind.RegularFile),
            new PhysicalFileMetadata(length, null, Now));

    private static PhysicalEntryObservation Directory(string id) =>
        PhysicalEntryObservation.Success(
            new PhysicalEntryIdentity("test", 1, "volume", id, PhysicalEntryKind.Directory));

    private sealed class Harness : IAsyncDisposable
    {
        private static readonly PathPolicy Paths = new(
            PathSyntax.Windows,
            PathCaseSensitivity.Insensitive,
            PathUnicodeNormalization.FormC);

        public Harness(bool subscribe = true, TimeProvider? timeProvider = null)
        {
            Factory = new ScriptedSourceFactory();
            Observations = new ScriptedObservationPort(Paths);
            Delay = new ControlledDelay();
            WatchRootConfiguration configuration = new(
                RootId,
                Canonical(@"C:\Watch"),
                isEnabled: true,
                includeSubdirectories: true);
            Monitor = new WatchRootMonitor(
                configuration,
                Factory,
                Observations,
                Delay,
                timeProvider ?? new FixedTimeProvider(Now),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(3));
            if (subscribe)
            {
                Monitor.Notification += (_, notification) => Notifications.Enqueue(notification);
            }
        }

        public WatchRootMonitor Monitor { get; }

        public ScriptedSourceFactory Factory { get; }

        public ScriptedObservationPort Observations { get; }

        public ControlledDelay Delay { get; }

        public ConcurrentQueue<WatchRootNotification> Notifications { get; } = new();

        public ScriptedSource Source => Factory.Sources[0];

        public IEnumerable<StableFileCandidate> Candidates =>
            Notifications.Where(
                    static notification => notification.Kind == WatchRootNotificationKind.Candidate)
                .Select(static notification => notification.Candidate!);

        public static string File(string name) => @"C:\Watch\" + name;

        public static async Task<Harness> SynchronizedAsync()
        {
            Harness harness = new();
            await harness.SynchronizeAsync();
            return harness;
        }

        public async Task SynchronizeAsync()
        {
            Monitor.Start();
            WatchBaselineToken token = Monitor.CaptureBaselineToken();
            Assert.True(Monitor.AcknowledgeBaseline(token));
            await Monitor.DrainAsync(TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync() => Monitor.DisposeAsync();

        private static CanonicalPath Canonical(string path)
        {
            PathResolution resolution = Paths.CanonicalizeAbsolute(path);
            Assert.True(resolution.Succeeded, resolution.Problem?.Description);
            return resolution.Path!;
        }
    }

    private sealed class ScriptedSourceFactory : IFileChangeSourceFactory
    {
        private readonly object _gate = new();

        public List<ScriptedSource> Sources { get; } = [];

        public List<FileChangeSourceRequest> Requests { get; } = [];

        public FileChangeSourceOpenResult Open(FileChangeSourceRequest request)
        {
            lock (_gate)
            {
                Requests.Add(request);
                ScriptedSource source = new();
                Sources.Add(source);
                return FileChangeSourceOpenResult.Opened(source);
            }
        }
    }

    private sealed class ScriptedSource : IFileChangeSource
    {
        private readonly object _gate = new();
        private readonly ManualResetEventSlim _callbacksExited = new(initialState: true);
        private EventHandler<FileChangeSourceEvent>? _handlers;

        public event EventHandler<FileChangeSourceEvent>? EventReceived
        {
            add
            {
                lock (_gate)
                {
                    _handlers += value;
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

        public bool IsStarted { get; private set; }

        public bool IsDisposed { get; private set; }

        public bool JoinCallbacksOnDispose { get; set; }

        public bool HasSubscribers
        {
            get
            {
                lock (_gate)
                {
                    return _handlers is not null;
                }
            }
        }

        public void Start() => IsStarted = true;

        public void Raise(FileChangeSourceEvent change)
        {
            EventHandler<FileChangeSourceEvent>? handlers;
            lock (_gate)
            {
                handlers = _handlers;
            }

            if (JoinCallbacksOnDispose)
            {
                _callbacksExited.Reset();
            }

            try
            {
                handlers?.Invoke(this, change);
            }
            finally
            {
                if (JoinCallbacksOnDispose)
                {
                    _callbacksExited.Set();
                }
            }
        }

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            if (JoinCallbacksOnDispose)
            {
                _callbacksExited.Wait();
            }

            IsDisposed = true;
            _callbacksExited.Dispose();
        }
    }

    private sealed class ControlledDelay : IWatchDelay
    {
        private readonly object _gate = new();
        private readonly List<Request> _requests = [];

        public TimeSpan[] Active
        {
            get
            {
                lock (_gate)
                {
                    return _requests
                        .Where(static request => !request.Completion.Task.IsCompleted)
                        .Select(static request => request.Duration)
                        .ToArray();
                }
            }
        }

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource completion = new();
            CancellationTokenRegistration registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            completion.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            TaskCompletionSource requestAdded;
            lock (_gate)
            {
                _requests.Add(new Request(delay, completion));
                requestAdded = _requestAdded;
                _requestAdded = NewRequestSignal();
            }

            requestAdded.TrySetResult();

            return new ValueTask(completion.Task);
        }

        public async Task ReleaseNextAsync(WatchRootMonitor monitor)
        {
            ReleaseNext();
            await monitor.DrainAsync(TestContext.Current.CancellationToken);
            await monitor.DrainAsync(TestContext.Current.CancellationToken);
        }

        public async Task ReleaseDurationAsync(
            TimeSpan duration,
            WatchRootMonitor monitor)
        {
            Request request;
            lock (_gate)
            {
                request = _requests.First(
                    item => !item.Completion.Task.IsCompleted && item.Duration == duration);
            }

            request.Completion.TrySetResult();
            await monitor.DrainAsync(TestContext.Current.CancellationToken);
            await monitor.DrainAsync(TestContext.Current.CancellationToken);
        }

        public async Task WaitForActiveCountAsync(
            int count,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                Task requestAdded;
                lock (_gate)
                {
                    if (_requests.Count(
                            static request => !request.Completion.Task.IsCompleted) >= count)
                    {
                        return;
                    }

                    requestAdded = _requestAdded.Task;
                }

                await requestAdded.WaitAsync(cancellationToken);
            }
        }

        public void ReleaseNext()
        {
            Request request;
            lock (_gate)
            {
                request = _requests.First(static item => !item.Completion.Task.IsCompleted);
            }

            request.Completion.TrySetResult();
        }

        private TaskCompletionSource _requestAdded = NewRequestSignal();

        private static TaskCompletionSource NewRequestSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed record Request(TimeSpan Duration, TaskCompletionSource Completion);
    }

    private sealed class ScriptedObservationPort(PathPolicy paths) : IFileStabilityObservationPort
    {
        private readonly object _gate = new();
        private readonly Dictionary<
            CanonicalPath,
            Queue<Func<CancellationToken, ValueTask<PhysicalEntryObservation>>>> _results =
                new(paths.LogicalComparer);

        public List<string> Paths { get; } = [];

        public void Enqueue(string path, params PhysicalEntryObservation[] results)
        {
            PathResolution resolution = paths.CanonicalizeAbsolute(path);
            Assert.True(resolution.Succeeded, resolution.Problem?.Description);
            lock (_gate)
            {
                Queue<Func<CancellationToken, ValueTask<PhysicalEntryObservation>>> queue = [];
                foreach (PhysicalEntryObservation result in results)
                {
                    queue.Enqueue(_ => ValueTask.FromResult(result));
                }

                _results[resolution.Path!] = queue;
            }
        }

        public HeldObservation EnqueueHeld(string path)
        {
            PathResolution resolution = paths.CanonicalizeAbsolute(path);
            Assert.True(resolution.Succeeded, resolution.Problem?.Description);
            HeldObservation held = new();
            lock (_gate)
            {
                _results[resolution.Path!].Enqueue(held.ObserveAsync);
            }

            return held;
        }

        public ValueTask<PhysicalEntryObservation> ObserveAsync(
            CanonicalPath path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Func<CancellationToken, ValueTask<PhysicalEntryObservation>> step;
            lock (_gate)
            {
                Paths.Add(path.Value);
                step = _results[path].Dequeue();
            }

            return step(cancellationToken);
        }
    }

    private sealed class HeldObservation
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<PhysicalEntryObservation> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<PhysicalEntryObservation> ObserveAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return new ValueTask<PhysicalEntryObservation>(
                Completion.Task.WaitAsync(cancellationToken));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CallbackTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private Action? _callback;

        public void InvokeOnce(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            Interlocked.Exchange(ref _callback, callback);
        }

        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Exchange(ref _callback, null)?.Invoke();
            return now;
        }
    }

    private sealed class BlockingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public override DateTimeOffset GetUtcNow()
        {
            Started.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
            return now;
        }
    }
}
