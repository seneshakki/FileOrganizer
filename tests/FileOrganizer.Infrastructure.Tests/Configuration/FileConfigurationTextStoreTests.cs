using System.Runtime.InteropServices;
using System.Text;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Infrastructure.Configuration;

namespace FileOrganizer.Infrastructure.Tests.Configuration;

[Collection(WindowsConfigurationPublicationTestGroup.Name)]
public sealed class FileConfigurationTextStoreTests
{
    [Fact]
    public async Task SaveAndLoadRoundTripsUnicodeAsUtf8WithoutABom()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "ayarlar", "düzenleyici.json");
        const string content = "{\n  \"name\": \"İndirilenler 📁\"\n}";
        FileConfigurationTextStore store = new();

        ConfigurationTextWriteResult write = await store.WriteAsync(
            Request(path, content),
            TestContext.Current.CancellationToken);
        ConfigurationTextReadResult read = await store.ReadAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Succeeded, write.Status);
        Assert.Empty(write.Problems);
        Assert.Equal(ConfigurationTextReadStatus.Found, read.Status);
        Assert.Equal(content, read.Content);
        Assert.Empty(read.Problems);
        Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public async Task ASecondSaveAtomicallyReplacesThePreviousDocument()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        FileConfigurationTextStore store = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.WriteAsync(Request(path, "{\"version\":1}"), token);

        ConfigurationTextWriteResult result = await store.WriteAsync(
            Request(path, "{\"version\":2}"),
            token);

        Assert.Equal(ConfigurationTextWriteStatus.Succeeded, result.Status);
        Assert.Equal("{\"version\":2}", File.ReadAllText(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task FailedReplacementPreservesThePreviousValidDocument()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        CancellationToken token = TestContext.Current.CancellationToken;
        FileConfigurationTextStore initialStore = new();
        await initialStore.WriteAsync(Request(path, "{\"version\":1}"), token);
        FileConfigurationTextStore failingStore = new(new FailingPublisher());

        ConfigurationTextWriteResult result = await failingStore.WriteAsync(
            Request(path, "{\"version\":2}"),
            token);

        Assert.Equal(ConfigurationTextWriteStatus.Failed, result.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.PublicationFailure,
            Assert.Single(result.Problems).Kind);
        Assert.Equal("{\"version\":1}", File.ReadAllText(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task FailedInitialPublicationLeavesNoTargetOrTemporaryFile()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        FileConfigurationTextStore store = new(new FailingPublisher());

        ConfigurationTextWriteResult result = await store.WriteAsync(
            Request(path, "{}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Failed, result.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.PublicationFailure,
            Assert.Single(result.Problems).Kind);
        Assert.False(File.Exists(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task InvalidUtf8ReturnsAStructuredReadFailure()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        await File.WriteAllBytesAsync(
            path,
            [0x7b, 0xff, 0x7d],
            TestContext.Current.CancellationToken);
        FileConfigurationTextStore store = new();

        ConfigurationTextReadResult result = await store.ReadAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextReadStatus.Failed, result.Status);
        Assert.Null(result.Content);
        Assert.Equal(
            ConfigurationStorageProblemKind.ReadFailure,
            Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public async Task Utf8PreambleIsAcceptedButNotReturnedAsContent()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        byte[] content = "{}"u8.ToArray();
        await File.WriteAllBytesAsync(
            path,
            [.. Encoding.UTF8.Preamble, .. content],
            TestContext.Current.CancellationToken);
        FileConfigurationTextStore store = new();

        ConfigurationTextReadResult result = await store.ReadAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextReadStatus.Found, result.Status);
        Assert.Equal("{}", result.Content);
    }

    [Fact]
    public async Task RelativeAndDirectoryPathsAreRejectedStructurally()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        FileConfigurationTextStore store = new();

        ConfigurationTextReadResult relativeRead = await store.ReadAsync(
            "configuration.json",
            TestContext.Current.CancellationToken);
        ConfigurationTextWriteResult directoryWrite = await store.WriteAsync(
            Request(temp.FullPath, "{}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextReadStatus.Failed, relativeRead.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.InvalidPath,
            Assert.Single(relativeRead.Problems).Kind);
        Assert.Equal(ConfigurationTextWriteStatus.Failed, directoryWrite.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.InvalidPath,
            Assert.Single(directoryWrite.Problems).Kind);
    }

    [Fact]
    public async Task MissingDocumentDoesNotCreateItsParentDirectory()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string parent = Path.Combine(temp.FullPath, "not-created");
        string path = Path.Combine(parent, "configuration.json");
        FileConfigurationTextStore store = new();

        ConfigurationTextReadResult result = await store.ReadAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextReadStatus.NotFound, result.Status);
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public async Task SaveCreatesOnlyItsConfigurationParent()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string parent = Path.Combine(temp.FullPath, "created", "for-config");
        string path = Path.Combine(parent, "configuration.json");
        FileConfigurationTextStore store = new();

        ConfigurationTextWriteResult result = await store.WriteAsync(
            Request(path, "{}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Succeeded, result.Status);
        Assert.Equal([path], Directory.GetFiles(parent));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task PreCanceledOperationsDoNotReadOrWrite()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        File.WriteAllText(path, "original");
        FileConfigurationTextStore store = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.ReadAsync(path, cancellation.Token).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.WriteAsync(Request(path, "changed"), cancellation.Token).AsTask());

        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task CreateIfAbsentDoesNotReplaceAFileThatAppearsDuringPublication()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        FileConfigurationTextStore store = new(new RacingCreatePublisher("external"));

        ConfigurationTextWriteResult result = await store.WriteAsync(
            Request(
                path,
                "created-by-editor",
                ConfigurationWritePrecondition.MustBeMissing),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Conflict, result.Status);
        Assert.Equal(
            ConfigurationWriteConflictKind.TargetAlreadyExists,
            result.Conflict!.Kind);
        Assert.Equal("external", File.ReadAllText(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task ExpectedRevisionRejectsAnExternalContentChange()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        await File.WriteAllTextAsync(
            path,
            "original",
            TestContext.Current.CancellationToken);
        FileConfigurationTextStore store = new();
        ConfigurationTextReadResult read = await store.ReadAsync(
            path,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            path,
            "external",
            TestContext.Current.CancellationToken);

        ConfigurationTextWriteResult result = await store.WriteAsync(
            Request(
                path,
                "draft",
                ConfigurationWritePrecondition.MustMatch(read.Revision!)),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Conflict, result.Status);
        Assert.Equal(
            ConfigurationWriteConflictKind.ChangedExternally,
            result.Conflict!.Kind);
        Assert.Equal("external", File.ReadAllText(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task ParticipatingWritersCannotInterleaveFinalValidationAndReplacement()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named mutex test.");
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        await File.WriteAllTextAsync(
            path,
            "original",
            TestContext.Current.CancellationToken);
        ConfigurationTextReadResult read = await new FileConfigurationTextStore().ReadAsync(
            path,
            TestContext.Current.CancellationToken);
        using var firstPublisher = new BlockingReplacingPublisher();
        var secondPublisher = new CountingPublisher();
        var secondGate = new SignalingPublicationGate(
            new WindowsConfigurationPublicationGate(TimeSpan.FromSeconds(10)));
        FileConfigurationTextStore firstStore = CreateIsolatedStore(
            firstPublisher,
            new WindowsConfigurationPublicationGate(TimeSpan.FromSeconds(10)));
        FileConfigurationTextStore secondStore = CreateIsolatedStore(
            secondPublisher,
            secondGate);
        ConfigurationTextWriteRequest firstRequest = Request(
            path,
            "first",
            ConfigurationWritePrecondition.MustMatch(read.Revision!));
        ConfigurationTextWriteRequest secondRequest = Request(
            path,
            "second",
            ConfigurationWritePrecondition.MustMatch(read.Revision!));

        Task<ConfigurationTextWriteResult> first = Task.Run(
            async () => await firstStore.WriteAsync(
                firstRequest,
                TestContext.Current.CancellationToken));
        await firstPublisher.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task<ConfigurationTextWriteResult> second = Task.Run(
            async () => await secondStore.WriteAsync(
                secondRequest,
                TestContext.Current.CancellationToken));
        await secondGate.AcquisitionAttempted.Task.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.False(second.IsCompleted);
        firstPublisher.AllowReplacement();

        ConfigurationTextWriteResult firstResult = await first;
        ConfigurationTextWriteResult secondResult = await second;
        Assert.Equal(ConfigurationTextWriteStatus.Succeeded, firstResult.Status);
        Assert.Equal(ConfigurationTextWriteStatus.Conflict, secondResult.Status);
        Assert.Equal(
            ConfigurationWriteConflictKind.ChangedExternally,
            secondResult.Conflict!.Kind);
        Assert.Equal(0, secondPublisher.PublicationCount);
        Assert.Equal("first", File.ReadAllText(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task SameProcessPathWaitTimeoutAndCancellationAreStructuredAndBounded()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows publication gate test.");
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        await File.WriteAllTextAsync(
            path,
            "original",
            TestContext.Current.CancellationToken);
        ConfigurationTextReadResult read = await new FileConfigurationTextStore().ReadAsync(
            path,
            TestContext.Current.CancellationToken);
        using var firstPublisher = new BlockingReplacingPublisher();
        var sharedPathGates = new ConfigurationPathGateRegistry();
        FileConfigurationTextStore firstStore = CreateIsolatedStore(
            firstPublisher,
            new WindowsConfigurationPublicationGate(TimeSpan.FromSeconds(10)),
            sharedPathGates);
        FileConfigurationTextStore timeoutStore = CreateIsolatedStore(
            new CountingPublisher(),
            new WindowsConfigurationPublicationGate(TimeSpan.Zero),
            sharedPathGates);
        FileConfigurationTextStore cancellationStore = CreateIsolatedStore(
            new CountingPublisher(),
            new WindowsConfigurationPublicationGate(TimeSpan.FromSeconds(10)),
            sharedPathGates);
        ConfigurationTextWriteRequest request = Request(
            path,
            "first",
            ConfigurationWritePrecondition.MustMatch(read.Revision!));

        Task<ConfigurationTextWriteResult> first = Task.Run(
            async () => await firstStore.WriteAsync(
                request,
                TestContext.Current.CancellationToken));
        await firstPublisher.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        ConfigurationTextWriteResult timedOut = await timeoutStore.WriteAsync(
            request,
            TestContext.Current.CancellationToken);
        Assert.Equal(ConfigurationTextWriteStatus.Failed, timedOut.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.PublicationSynchronizationUnavailable,
            Assert.Single(timedOut.Problems).Kind);

        using var cancellation = new CancellationTokenSource();
        Task<ConfigurationTextWriteResult> canceledWrite = cancellationStore.WriteAsync(
            request,
            cancellation.Token).AsTask();
        Assert.False(canceledWrite.IsCompleted);
        await cancellation.CancelAsync();
        ConfigurationTextWriteResult canceled = await canceledWrite;
        Assert.Equal(ConfigurationTextWriteStatus.Failed, canceled.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.PublicationSynchronizationCanceled,
            Assert.Single(canceled.Problems).Kind);

        firstPublisher.AllowReplacement();
        Assert.Equal(ConfigurationTextWriteStatus.Succeeded, (await first).Status);
        Assert.Equal("first", File.ReadAllText(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task PublicationGateCancellationReturnsStructuredFailureWithoutWriting()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named mutex test.");
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        using var owner = new BoundedNamedMutexOwner(
            WindowsConfigurationPublicationGate.MutexName);
        owner.StartAndWait();
        var signalingGate = new SignalingPublicationGate(
            new WindowsConfigurationPublicationGate(TimeSpan.FromSeconds(10)));
        FileConfigurationTextStore store = CreateIsolatedStore(
            new DurableConfigurationFilePublisher(),
            signalingGate);
        using var cancellation = new CancellationTokenSource();

        Task<ConfigurationTextWriteResult> write = store.WriteAsync(
            Request(path, "draft"),
            cancellation.Token).AsTask();
        await signalingGate.AcquisitionAttempted.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        ConfigurationTextWriteResult result = await write;
        Assert.Equal(ConfigurationTextWriteStatus.Failed, result.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.PublicationSynchronizationCanceled,
            Assert.Single(result.Problems).Kind);
        Assert.False(File.Exists(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task PublicationGateTimeoutReturnsStructuredFailureWithoutWriting()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named mutex test.");
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        using var owner = new BoundedNamedMutexOwner(
            WindowsConfigurationPublicationGate.MutexName);
        owner.StartAndWait();
        FileConfigurationTextStore store = CreateIsolatedStore(
            new DurableConfigurationFilePublisher(),
            new WindowsConfigurationPublicationGate(TimeSpan.Zero));

        ConfigurationTextWriteResult result = await store.WriteAsync(
            Request(path, "draft"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Failed, result.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.PublicationSynchronizationUnavailable,
            Assert.Single(result.Problems).Kind);
        Assert.False(File.Exists(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task AbandonedPublicationOwnerForcesFreshValidationBeforeWriting()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named mutex test.");
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        await File.WriteAllTextAsync(
            path,
            "original",
            TestContext.Current.CancellationToken);
        FileConfigurationTextStore reader = new();
        ConfigurationTextReadResult read = await reader.ReadAsync(
            path,
            TestContext.Current.CancellationToken);
        using var observer = new Mutex(
            initiallyOwned: false,
            WindowsConfigurationPublicationGate.MutexName);
        using var ownerReady = new ManualResetEventSlim();
        int abandonedOwnerAcquired = 0;
        var abandonedOwner = new Thread(
            () =>
            {
                using Mutex owner = Mutex.OpenExisting(
                    WindowsConfigurationPublicationGate.MutexName);
                bool acquired;
                try
                {
                    acquired = owner.WaitOne(TimeSpan.FromSeconds(10));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (acquired)
                {
                    Interlocked.Exchange(ref abandonedOwnerAcquired, 1);
                }

                ownerReady.Set();
            });
        abandonedOwner.Start();
        Assert.True(ownerReady.Wait(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref abandonedOwnerAcquired));
        Assert.True(abandonedOwner.Join(TimeSpan.FromSeconds(10)));
        await File.WriteAllTextAsync(
            path,
            "external",
            TestContext.Current.CancellationToken);
        var observingGate = new ObservingPublicationGate(
            new WindowsConfigurationPublicationGate(TimeSpan.FromSeconds(10)));
        FileConfigurationTextStore store = CreateIsolatedStore(
            new DurableConfigurationFilePublisher(),
            observingGate);

        ConfigurationTextWriteResult result = await store.WriteAsync(
            Request(
                path,
                "draft",
                ConfigurationWritePrecondition.MustMatch(read.Revision!)),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Conflict, result.Status);
        Assert.Equal(
            ConfigurationWriteConflictKind.ChangedExternally,
            result.Conflict!.Kind);
        Assert.True(observingGate.PreviousOwnerAbandoned);
        Assert.Equal("external", File.ReadAllText(path));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task ExportRejectsAHardLinkAliasOfAProtectedConfiguration()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows physical identity test.");
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string activePath = Path.Combine(temp.FullPath, "active.json");
        string aliasPath = Path.Combine(temp.FullPath, "alias.json");
        await File.WriteAllTextAsync(
            activePath,
            "active",
            TestContext.Current.CancellationToken);
        Assert.True(
            CreateHardLink(aliasPath, activePath, 0),
            $"CreateHardLinkW failed with {Marshal.GetLastPInvokeError()}.");
        FileConfigurationTextStore store = new();

        ConfigurationTextWriteResult result = await store.WriteAsync(
            Request(
                aliasPath,
                "export",
                protectedPaths: [activePath]),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Conflict, result.Status);
        Assert.Equal(
            ConfigurationWriteConflictKind.ProtectedPathAlias,
            result.Conflict!.Kind);
        Assert.Equal("active", File.ReadAllText(activePath));
        Assert.Equal("active", File.ReadAllText(aliasPath));
        Assert.Empty(TemporaryFiles(aliasPath));
    }

    [Fact]
    public async Task ExportRejectsAnExtendedPathAliasOfAProtectedConfiguration()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows extended-path identity test.");
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string activePath = Path.Combine(temp.FullPath, "active.json");
        await File.WriteAllTextAsync(
            activePath,
            "active",
            TestContext.Current.CancellationToken);
        string extendedAlias = @"\\?\" + activePath;
        FileConfigurationTextStore store = new();

        ConfigurationTextWriteResult result = await store.WriteAsync(
            Request(
                extendedAlias,
                "export",
                protectedPaths: [activePath]),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Conflict, result.Status);
        Assert.Equal(
            ConfigurationWriteConflictKind.ProtectedPathAlias,
            result.Conflict!.Kind);
        Assert.Equal("active", File.ReadAllText(activePath));
        Assert.Empty(TemporaryFiles(activePath));
    }

    [Fact]
    public async Task ExportFailsClosedWhenAProtectedConfigurationIsNoLongerARegularFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows physical identity test.");
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string destinationPath = Path.Combine(temp.FullPath, "export.json");
        string protectedPath = Path.Combine(temp.FullPath, "active.json");
        Directory.CreateDirectory(protectedPath);
        FileConfigurationTextStore store = new();

        ConfigurationTextWriteResult result = await store.WriteAsync(
            Request(
                destinationPath,
                "export",
                protectedPaths: [protectedPath]),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Conflict, result.Status);
        Assert.Equal(
            ConfigurationWriteConflictKind.ProtectedPathIdentityUnavailable,
            result.Conflict!.Kind);
        Assert.False(File.Exists(destinationPath));
        Assert.Empty(TemporaryFiles(destinationPath));
    }

    [Fact]
    public async Task ExportFailsSafeWhenPhysicalDistinctnessCannotBeEstablished()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string activePath = Path.Combine(temp.FullPath, "active.json");
        string exportPath = Path.Combine(temp.FullPath, "export.json");
        await File.WriteAllTextAsync(
            activePath,
            "active",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            exportPath,
            "existing export",
            TestContext.Current.CancellationToken);
        FileConfigurationTextStore store = new(
            new DurableConfigurationFilePublisher(),
            new UnverifiablePathIdentityComparer());

        ConfigurationTextWriteResult result = await store.WriteAsync(
            Request(
                exportPath,
                "replacement export",
                protectedPaths: [activePath]),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationTextWriteStatus.Conflict, result.Status);
        Assert.Equal(
            ConfigurationWriteConflictKind.ProtectedPathIdentityUnavailable,
            result.Conflict!.Kind);
        Assert.Equal("existing export", File.ReadAllText(exportPath));
        Assert.Empty(TemporaryFiles(exportPath));
    }

    private static string[] TemporaryFiles(string destinationPath) =>
        Directory.GetFiles(
            Path.GetDirectoryName(destinationPath)!,
            ".fileorganizer-config-*.tmp",
            SearchOption.TopDirectoryOnly);

    private static ConfigurationTextWriteRequest Request(
        string path,
        string content,
        ConfigurationWritePrecondition? precondition = null,
        IEnumerable<string>? protectedPaths = null) =>
        new(
            path,
            content,
            precondition ?? ConfigurationWritePrecondition.Unconditional,
            protectedPaths);

    private static FileConfigurationTextStore CreateIsolatedStore(
        IConfigurationFilePublisher publisher,
        IConfigurationPublicationGate publicationGate,
        ConfigurationPathGateRegistry? pathGates = null) =>
        new(
            publisher,
            new WindowsConfigurationPathIdentityComparer(),
            publicationGate,
            pathGates ?? new ConfigurationPathGateRegistry());

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        nint securityAttributes);

    private sealed class FailingPublisher : IConfigurationFilePublisher
    {
        public void PublishNew(string temporaryPath, string destinationPath) =>
            throw new IOException("Injected initial publication failure.");

        public void ReplaceExisting(string temporaryPath, string destinationPath) =>
            throw new IOException("Injected replacement failure.");
    }

    private sealed class RacingCreatePublisher(string externalContent)
        : IConfigurationFilePublisher
    {
        public void PublishNew(string temporaryPath, string destinationPath)
        {
            File.WriteAllText(destinationPath, externalContent);
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }

        public void ReplaceExisting(string temporaryPath, string destinationPath) =>
            throw new InvalidOperationException("The create-only test cannot replace a target.");
    }

    private sealed class UnverifiablePathIdentityComparer : IConfigurationPathIdentityComparer
    {
        public ConfigurationPathIdentityComparison CompareExisting(
            string firstPath,
            string secondPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ConfigurationPathIdentityComparison.Unverifiable;
        }
    }

    private sealed class BlockingReplacingPublisher : IConfigurationFilePublisher, IDisposable
    {
        private readonly ManualResetEventSlim _allowReplacement = new();
        private readonly DurableConfigurationFilePublisher _inner = new();

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void AllowReplacement() => _allowReplacement.Set();

        public void PublishNew(string temporaryPath, string destinationPath) =>
            throw new InvalidOperationException("This test requires replacement.");

        public void ReplaceExisting(string temporaryPath, string destinationPath)
        {
            Entered.TrySetResult();
            if (!_allowReplacement.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The replacement test was not released.");
            }

            _inner.ReplaceExisting(temporaryPath, destinationPath);
        }

        public void Dispose() => _allowReplacement.Dispose();
    }

    private sealed class CountingPublisher : IConfigurationFilePublisher
    {
        private readonly DurableConfigurationFilePublisher _inner = new();
        private int _publicationCount;

        internal int PublicationCount => Volatile.Read(ref _publicationCount);

        public void PublishNew(string temporaryPath, string destinationPath)
        {
            Interlocked.Increment(ref _publicationCount);
            _inner.PublishNew(temporaryPath, destinationPath);
        }

        public void ReplaceExisting(string temporaryPath, string destinationPath)
        {
            Interlocked.Increment(ref _publicationCount);
            _inner.ReplaceExisting(temporaryPath, destinationPath);
        }
    }

    private sealed class SignalingPublicationGate(IConfigurationPublicationGate inner)
        : IConfigurationPublicationGate
    {
        internal TaskCompletionSource AcquisitionAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan AcquisitionTimeout => inner.AcquisitionTimeout;

        public ValueTask<IConfigurationPublicationLease> AcquireAsync(
            string canonicalPathKey,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            AcquisitionAttempted.TrySetResult();
            return inner.AcquireAsync(canonicalPathKey, timeout, cancellationToken);
        }
    }

    private sealed class ObservingPublicationGate(IConfigurationPublicationGate inner)
        : IConfigurationPublicationGate
    {
        internal bool PreviousOwnerAbandoned { get; private set; }

        public TimeSpan AcquisitionTimeout => inner.AcquisitionTimeout;

        public async ValueTask<IConfigurationPublicationLease> AcquireAsync(
            string canonicalPathKey,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            IConfigurationPublicationLease lease = await inner.AcquireAsync(
                canonicalPathKey,
                timeout,
                cancellationToken);
            PreviousOwnerAbandoned = lease.PreviousOwnerAbandoned;
            return lease;
        }
    }

}
