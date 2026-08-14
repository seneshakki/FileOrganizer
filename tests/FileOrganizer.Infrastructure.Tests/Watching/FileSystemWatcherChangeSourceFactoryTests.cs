using FileOrganizer.Application.Watching;
using FileOrganizer.Core.Paths;
using FileOrganizer.Infrastructure.Watching;

namespace FileOrganizer.Infrastructure.Tests.Watching;

public sealed class FileSystemWatcherChangeSourceFactoryTests
{
    private static readonly Guid WatchRootId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenConfiguresADormantWatcherWithDeliberateFilters(
        bool includeSubdirectories)
    {
        FakeWatcherSession session = new();
        FakeWatcherSessionFactory sessionFactory = new(session);
        FileSystemWatcherChangeSourceFactory factory = new(sessionFactory);

        FileChangeSourceOpenResult result = factory.Open(Request(includeSubdirectories));

        Assert.Equal(FileChangeSourceOpenStatus.Opened, result.Status);
        Assert.Equal(HostRoot().Value, sessionFactory.RequestedPath);
        Assert.Equal("*", session.Filter);
        Assert.Equal(FileSystemWatcherChangeSourceFactory.ObservedChanges, session.NotifyFilter);
        Assert.Equal(includeSubdirectories, session.IncludeSubdirectories);
        Assert.False(session.EnableRaisingEvents);
        Assert.Equal(5, session.TotalHandlerCount);

        result.Source!.Dispose();
        Assert.True(session.IsDisposed);
        Assert.Equal(0, session.TotalHandlerCount);
    }

    [Fact]
    public void SourceTranslatesAllNativeHintsAndAssignsIncreasingSequence()
    {
        FakeWatcherSession session = new();
        FileChangeSourceOpenResult opened = new FileSystemWatcherChangeSourceFactory(
            new FakeWatcherSessionFactory(session)).Open(Request(includeSubdirectories: true));
        using IFileChangeSource source = opened.Source!;
        List<FileChangeSourceEvent> observed = [];
        source.EventReceived += (_, change) => observed.Add(change);
        source.Start();

        string directory = HostRoot().Value;
        session.RaiseCreated(directory, "created.txt");
        session.RaiseChanged(directory, "changed.txt");
        session.RaiseRenamed(directory, "renamed.txt", "before.txt");
        session.RaiseDeleted(directory, "deleted.txt");
        session.RaiseError(new IOException("Machine-specific detail must not escape."));
        session.RaiseError(new InternalBufferOverflowException("Overflow detail."));

        Assert.Equal(
            [
                FileChangeSourceEventKind.Created,
                FileChangeSourceEventKind.Changed,
                FileChangeSourceEventKind.Renamed,
                FileChangeSourceEventKind.Deleted,
                FileChangeSourceEventKind.WatcherError,
                FileChangeSourceEventKind.Overflow,
            ],
            observed.Select(static change => change.Kind));
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], observed.Select(static change => change.Sequence));
        Assert.Equal(Path.Combine(directory, "created.txt"), observed[0].Path);
        Assert.Equal(Path.Combine(directory, "renamed.txt"), observed[2].Path);
        Assert.Equal(Path.Combine(directory, "before.txt"), observed[2].OldPath);
        Assert.Equal(FileChangeSourceProblemKind.InputOutputFailure, observed[4].Problem!.Kind);
        Assert.DoesNotContain(
            "Machine-specific",
            observed[4].Problem!.Description,
            StringComparison.Ordinal);
        Assert.Equal(FileChangeSourceProblemKind.BufferOverflow, observed[5].Problem!.Kind);
    }

    [Fact]
    public void StartIsIdempotentAndDisposeRemovesHandlersAndStopsDelivery()
    {
        FakeWatcherSession session = new();
        IFileChangeSource source = new FileSystemWatcherChangeSourceFactory(
            new FakeWatcherSessionFactory(session)).Open(Request()).Source!;
        int received = 0;
        source.EventReceived += (_, _) => received++;

        source.Start();
        source.Start();
        Assert.Equal(1, session.EnableCount);

        source.Dispose();
        source.Dispose();
        session.RaiseCreated(HostRoot().Value, "ignored.txt");

        Assert.Equal(0, received);
        Assert.True(session.IsDisposed);
        Assert.Equal(0, session.TotalHandlerCount);
        Assert.Throws<ObjectDisposedException>(source.Start);
    }

    [Fact]
    public void HintsBeforeStartAreSuppressedWithoutConsumingSequence()
    {
        FakeWatcherSession session = new();
        using IFileChangeSource source = new FileSystemWatcherChangeSourceFactory(
            new FakeWatcherSessionFactory(session)).Open(Request()).Source!;
        List<FileChangeSourceEvent> observed = [];
        source.EventReceived += (_, change) => observed.Add(change);

        session.RaiseCreated(HostRoot().Value, "before-start.txt");
        source.Start();
        session.RaiseCreated(HostRoot().Value, "after-start.txt");

        FileChangeSourceEvent change = Assert.Single(observed);
        Assert.Equal(1, change.Sequence);
        Assert.EndsWith("after-start.txt", change.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void StartFailureBecomesAStructuredWatcherError()
    {
        FakeWatcherSession session = new()
        {
            EnableFailure = new UnauthorizedAccessException("Secret path."),
        };
        using IFileChangeSource source = new FileSystemWatcherChangeSourceFactory(
            new FakeWatcherSessionFactory(session)).Open(Request()).Source!;
        FileChangeSourceEvent? observed = null;
        int received = 0;
        source.EventReceived += (_, change) =>
        {
            observed = change;
            received++;
        };

        source.Start();
        source.Start();

        Assert.NotNull(observed);
        Assert.Equal(1, received);
        Assert.Equal(FileChangeSourceEventKind.WatcherError, observed.Kind);
        Assert.Equal(1, observed.Sequence);
        Assert.Equal(FileChangeSourceProblemKind.Inaccessible, observed.Problem!.Kind);
        Assert.DoesNotContain("Secret", observed.Problem.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowingSubscriberCannotEscapeCallbackOrStarveOtherSubscribers()
    {
        FakeWatcherSession session = new();
        using IFileChangeSource source = new FileSystemWatcherChangeSourceFactory(
            new FakeWatcherSessionFactory(session)).Open(Request()).Source!;
        List<long> receivedSequences = [];
        source.EventReceived += (_, _) =>
            throw new InvalidOperationException("Injected consumer failure.");
        source.EventReceived += (_, change) => receivedSequences.Add(change.Sequence);
        source.Start();

        session.RaiseCreated(HostRoot().Value, "first.txt");
        session.RaiseChanged(HostRoot().Value, "second.txt");

        Assert.Equal([1L, 2L], receivedSequences);
    }

    [Fact]
    public async Task StartFailureCannotPublishAfterConcurrentDispose()
    {
        using ManualResetEventSlim enableEntered = new();
        using ManualResetEventSlim continueEnable = new();
        FakeWatcherSession session = new()
        {
            EnableEntered = enableEntered,
            ContinueEnable = continueEnable,
            EnableFailure = new IOException("Injected start failure."),
        };
        IFileChangeSource source = new FileSystemWatcherChangeSourceFactory(
            new FakeWatcherSessionFactory(session)).Open(Request()).Source!;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        int received = 0;
        source.EventReceived += (_, _) => received++;

        Task start = Task.Run(source.Start, cancellationToken);
        Assert.True(enableEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken));
        source.Dispose();
        continueEnable.Set();
        await start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, received);
        Assert.True(session.IsDisposed);
        Assert.Equal(0, session.TotalHandlerCount);
    }

    [Fact]
    public async Task DisposeDoesNotWaitForABlockedSubscriber()
    {
        using ManualResetEventSlim subscriberEntered = new();
        using ManualResetEventSlim releaseSubscriber = new();
        FakeWatcherSession session = new();
        IFileChangeSource source = new FileSystemWatcherChangeSourceFactory(
            new FakeWatcherSessionFactory(session)).Open(Request()).Source!;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        source.EventReceived += (_, _) =>
        {
            subscriberEntered.Set();
            releaseSubscriber.Wait(cancellationToken);
        };
        source.Start();

        Task callback = Task.Run(
            () => session.RaiseCreated(HostRoot().Value, "blocked.txt"),
            cancellationToken);
        Assert.True(subscriberEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken));
        Task dispose = Task.Run(source.Dispose, cancellationToken);
        try
        {
            await dispose.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseSubscriber.Set();
        }

        await callback.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(session.IsDisposed);
        Assert.Equal(0, session.TotalHandlerCount);
    }

    [Fact]
    public async Task ConcurrentNativeCallbacksAreSerializedInSequenceOrder()
    {
        FakeWatcherSession session = new();
        using IFileChangeSource source = new FileSystemWatcherChangeSourceFactory(
            new FakeWatcherSessionFactory(session)).Open(Request()).Source!;
        List<long> observedSequences = [];
        source.EventReceived += (_, change) => observedSequences.Add(change.Sequence);
        source.Start();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Task[] callbacks = Enumerable.Range(0, 32)
            .Select(
                index => Task.Run(
                    () => session.RaiseChanged(HostRoot().Value, $"{index:D2}.txt"),
                    cancellationToken))
            .ToArray();
        await Task.WhenAll(callbacks);

        Assert.Equal(Enumerable.Range(1, 32).Select(static value => (long)value), observedSequences);
    }

    [Fact]
    public async Task BlockedSubscriberSaturationCollapsesPendingHintsIntoOneOverflow()
    {
        using ManualResetEventSlim subscriberEntered = new();
        using ManualResetEventSlim releaseSubscriber = new();
        FakeWatcherSession session = new();
        using IFileChangeSource source = new FileSystemWatcherChangeSourceFactory(
            new FakeWatcherSessionFactory(session)).Open(Request()).Source!;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        List<FileChangeSourceEvent> observed = [];
        source.EventReceived += (_, change) =>
        {
            observed.Add(change);
            if (change.Sequence == 1)
            {
                subscriberEntered.Set();
                releaseSubscriber.Wait(cancellationToken);
            }
        };
        source.Start();

        Task blockedCallback = Task.Run(
            () => session.RaiseCreated(HostRoot().Value, "first.txt"),
            cancellationToken);
        Assert.True(subscriberEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken));

        int excessHints = FileSystemWatcherChangeSourceFactory.PendingChangeCapacity + 32;
        for (int index = 0; index < excessHints; index++)
        {
            session.RaiseChanged(HostRoot().Value, $"{index:D3}.txt");
        }

        session.RaiseError(new InternalBufferOverflowException("A second overflow."));
        session.RaiseDeleted(HostRoot().Value, "ignored-after-overflow.txt");
        releaseSubscriber.Set();
        await blockedCallback.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Collection(
            observed,
            static change => Assert.Equal(FileChangeSourceEventKind.Created, change.Kind),
            static change =>
            {
                Assert.Equal(FileChangeSourceEventKind.Overflow, change.Kind);
                Assert.Equal(FileChangeSourceProblemKind.BufferOverflow, change.Problem!.Kind);
                Assert.True(change.Sequence > 2);
            });
    }

    [Fact]
    public void OpenFailureIsStructuredAndDisposesAPartiallyConfiguredWatcher()
    {
        FakeWatcherSession session = new()
        {
            ConfigurationFailure = new IOException("Injected configuration failure."),
        };
        FileSystemWatcherChangeSourceFactory factory = new(
            new FakeWatcherSessionFactory(session));

        FileChangeSourceOpenResult result = factory.Open(Request());

        Assert.Equal(FileChangeSourceOpenStatus.Unavailable, result.Status);
        Assert.Null(result.Source);
        Assert.Equal(FileChangeSourceProblemKind.InputOutputFailure, result.Problem!.Kind);
        Assert.True(session.IsDisposed);
        Assert.Equal(0, session.TotalHandlerCount);
    }

    [Fact]
    public void ForeignPathSyntaxIsExplicitlyUnsupportedWithoutOpeningAWatcher()
    {
        FakeWatcherSessionFactory sessionFactory = new(new FakeWatcherSession());
        FileSystemWatcherChangeSourceFactory factory = new(sessionFactory);
        FileChangeSourceRequest request = new(
            WatchRootId,
            ForeignRoot(),
            includeSubdirectories: false);

        FileChangeSourceOpenResult result = factory.Open(request);

        Assert.Equal(FileChangeSourceOpenStatus.Unsupported, result.Status);
        Assert.Equal(
            FileChangeSourceProblemKind.UnsupportedCapability,
            result.Problem!.Kind);
        Assert.Null(sessionFactory.RequestedPath);
    }

    private static FileChangeSourceRequest Request(bool includeSubdirectories = false) =>
        new(WatchRootId, HostRoot(), includeSubdirectories);

    private static CanonicalPath HostRoot()
    {
        PathPolicy policy = HostPolicy();
        string path = OperatingSystem.IsWindows() ? @"C:\Watch" : "/watch";
        return policy.CanonicalizeAbsolute(path).Path!;
    }

    private static CanonicalPath ForeignRoot()
    {
        PathPolicy policy = OperatingSystem.IsWindows()
            ? new PathPolicy(
                PathSyntax.Posix,
                PathCaseSensitivity.Sensitive,
                PathUnicodeNormalization.None)
            : new PathPolicy(
                PathSyntax.Windows,
                PathCaseSensitivity.Insensitive,
                PathUnicodeNormalization.None);
        string path = OperatingSystem.IsWindows() ? "/watch" : @"C:\Watch";
        return policy.CanonicalizeAbsolute(path).Path!;
    }

    private static PathPolicy HostPolicy() => OperatingSystem.IsWindows()
        ? new PathPolicy(
            PathSyntax.Windows,
            PathCaseSensitivity.Insensitive,
            PathUnicodeNormalization.None)
        : new PathPolicy(
            PathSyntax.Posix,
            PathCaseSensitivity.Sensitive,
            PathUnicodeNormalization.None);

    private sealed class FakeWatcherSessionFactory(FakeWatcherSession session)
        : IFileSystemWatcherSessionFactory
    {
        public string? RequestedPath { get; private set; }

        public IFileSystemWatcherSession Create(string path)
        {
            RequestedPath = path;
            return session;
        }
    }

    private sealed class FakeWatcherSession : IFileSystemWatcherSession
    {
        private FileSystemEventHandler? _changed;
        private FileSystemEventHandler? _created;
        private FileSystemEventHandler? _deleted;
        private RenamedEventHandler? _renamed;
        private ErrorEventHandler? _error;
        private string _filter = "*.*";
        private NotifyFilters _notifyFilter;

        public event FileSystemEventHandler? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public event FileSystemEventHandler? Created
        {
            add => _created += value;
            remove => _created -= value;
        }

        public event FileSystemEventHandler? Deleted
        {
            add => _deleted += value;
            remove => _deleted -= value;
        }

        public event RenamedEventHandler? Renamed
        {
            add => _renamed += value;
            remove => _renamed -= value;
        }

        public event ErrorEventHandler? Error
        {
            add => _error += value;
            remove => _error -= value;
        }

        public Exception? ConfigurationFailure { get; init; }

        public Exception? EnableFailure { get; init; }

        public ManualResetEventSlim? EnableEntered { get; init; }

        public ManualResetEventSlim? ContinueEnable { get; init; }

        public bool IsDisposed { get; private set; }

        public int EnableCount { get; private set; }

        public int TotalHandlerCount =>
            Count(_changed) + Count(_created) + Count(_deleted) + Count(_renamed) + Count(_error);

        public string Filter
        {
            get => _filter;
            set
            {
                if (ConfigurationFailure is not null)
                {
                    throw ConfigurationFailure;
                }

                _filter = value;
            }
        }

        public NotifyFilters NotifyFilter
        {
            get => _notifyFilter;
            set => _notifyFilter = value;
        }

        public bool IncludeSubdirectories { get; set; }

        public bool EnableRaisingEvents
        {
            get;
            set
            {
                if (value)
                {
                    EnableEntered?.Set();
                    ContinueEnable?.Wait();
                    if (EnableFailure is not null)
                    {
                        throw EnableFailure;
                    }
                }

                field = value;
                if (value)
                {
                    EnableCount++;
                }
            }
        }

        public void Dispose() => IsDisposed = true;

        internal void RaiseCreated(string directory, string name) =>
            _created?.Invoke(
                this,
                new FileSystemEventArgs(WatcherChangeTypes.Created, directory, name));

        internal void RaiseChanged(string directory, string name) =>
            _changed?.Invoke(
                this,
                new FileSystemEventArgs(WatcherChangeTypes.Changed, directory, name));

        internal void RaiseDeleted(string directory, string name) =>
            _deleted?.Invoke(
                this,
                new FileSystemEventArgs(WatcherChangeTypes.Deleted, directory, name));

        internal void RaiseRenamed(string directory, string name, string oldName) =>
            _renamed?.Invoke(
                this,
                new RenamedEventArgs(WatcherChangeTypes.Renamed, directory, name, oldName));

        internal void RaiseError(Exception exception) =>
            _error?.Invoke(this, new ErrorEventArgs(exception));

        private static int Count(Delegate? handlers) => handlers?.GetInvocationList().Length ?? 0;
    }
}
