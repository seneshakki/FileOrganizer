using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Application.Orchestration;
using FileOrganizer.Application.Recovery;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;
using FileOrganizer.Infrastructure.FileSystem;
using FileOrganizer.Infrastructure.Journaling;
using FileOrganizer.Infrastructure.Tests.Journaling;
using Microsoft.Win32.SafeHandles;

namespace FileOrganizer.Infrastructure.Tests.FileSystem;

[Collection(WindowsGlobalExecutionLeaseTestGroup.Name)]
public sealed class WindowsPhysicalMoveAdapterTests
{
    [Fact]
    public async Task MovesSameVolumeFileWithoutOverwriteAndPreservesIdentity()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "payload");
        string target = Path.Combine(destination, "report.txt");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        PhysicalEntryIdentity expected = manifest.Items[0].SourcePrecondition.PhysicalIdentity!;

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        var adapter = new WindowsPhysicalMoveAdapter();
        MoveLeaseAcquisition acquisition = await adapter.TryAcquireAsync(
            globalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);
        await using IMoveOperationLease moveLease = Assert.IsAssignableFrom<IMoveOperationLease>(
            acquisition.Lease);

        MoveAttemptResult attempt = await moveLease.CommitAsync();
        MoveObservation observation = await moveLease.ObserveAsync();

        Assert.Equal(MoveAttemptOutcome.Succeeded, attempt.Outcome);
        Assert.Equal(
            RecoveryDisposition.Applied,
            MoveRecoveryStateMachine.Classify(
                expected,
                observation.Source,
                observation.Target).Disposition);
        await moveLease.DisposeAsync();
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(target));
        Assert.Equal("payload", File.ReadAllText(target));
        Assert.Equal(expected, observation.Target.Identity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingTargetIsConflictAndNeverOverwritten(bool directoryTarget)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "source");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        string target = Path.Combine(destination, "report.txt");
        if (directoryTarget)
        {
            Directory.CreateDirectory(target);
        }
        else
        {
            File.WriteAllText(target, "existing");
        }

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        var adapter = new WindowsPhysicalMoveAdapter();
        MoveLeaseAcquisition acquisition = await adapter.TryAcquireAsync(
            globalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionReadiness.Conflict, acquisition.Readiness);
        Assert.Equal(ExecutionProblemKind.DestinationConflict, acquisition.Problem!.Kind);
        Assert.Equal("source", File.ReadAllText(source));
        Assert.Equal(directoryTarget, Directory.Exists(target));
        if (!directoryTarget)
        {
            Assert.Equal("existing", File.ReadAllText(target));
        }
    }

    [Fact]
    public async Task SourceMetadataDriftIsBlockedBeforeMutation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "old");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        File.AppendAllText(source, "changed");

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        var adapter = new WindowsPhysicalMoveAdapter();
        MoveLeaseAcquisition acquisition = await adapter.TryAcquireAsync(
            globalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionReadiness.Blocked, acquisition.Readiness);
        Assert.Equal(ExecutionProblemKind.SourceRevalidationFailed, acquisition.Problem!.Kind);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(destination, "report.txt")));
    }

    [Fact]
    public async Task SourceIdentityDriftIsBlockedBeforeMutation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "planned");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        File.Move(source, Path.Combine(sourceRoot, "original.txt"));
        File.WriteAllText(source, "replacement");

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        MoveLeaseAcquisition acquisition = await new WindowsPhysicalMoveAdapter().TryAcquireAsync(
            globalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionReadiness.Blocked, acquisition.Readiness);
        Assert.Equal(ExecutionProblemKind.SourceRevalidationFailed, acquisition.Problem!.Kind);
        Assert.Equal("replacement", File.ReadAllText(source));
        Assert.False(File.Exists(Path.Combine(destination, "report.txt")));
    }

    [Fact]
    public async Task SourceRootIdentityDriftIsBlockedBeforeMutation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "planned");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        Directory.Move(sourceRoot, Path.Combine(temporary.FullPath, "old-source"));
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(source, "replacement");

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        MoveLeaseAcquisition acquisition = await new WindowsPhysicalMoveAdapter().TryAcquireAsync(
            globalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionReadiness.Blocked, acquisition.Readiness);
        Assert.Equal(ExecutionProblemKind.SourceRevalidationFailed, acquisition.Problem!.Kind);
        Assert.False(File.Exists(Path.Combine(destination, "report.txt")));
    }

    [Fact]
    public async Task DestinationDirectoryIdentityDriftIsBlockedBeforeMutation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "planned");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        Directory.Move(destination, Path.Combine(temporary.FullPath, "old-destination"));
        Directory.CreateDirectory(destination);

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        MoveLeaseAcquisition acquisition = await new WindowsPhysicalMoveAdapter().TryAcquireAsync(
            globalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionReadiness.Blocked, acquisition.Readiness);
        Assert.Equal(
            ExecutionProblemKind.DestinationRevalidationFailed,
            acquisition.Problem!.Kind);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task SourceReparsePointIsBlockedWithoutFollowingIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "planned");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        string targetOfLink = Path.Combine(sourceRoot, "original.txt");
        File.Move(source, targetOfLink);
        TryCreateFileLinkOrSkip(source, targetOfLink);

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        MoveLeaseAcquisition acquisition = await new WindowsPhysicalMoveAdapter().TryAcquireAsync(
            globalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionReadiness.Blocked, acquisition.Readiness);
        Assert.Equal(ExecutionProblemKind.SourceRevalidationFailed, acquisition.Problem!.Kind);
        Assert.Equal("planned", File.ReadAllText(targetOfLink));
        Assert.False(File.Exists(Path.Combine(destination, "report.txt")));
    }

    [Fact]
    public async Task DestinationReparsePointIsBlockedWithoutFollowingIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "planned");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        string targetOfLink = Path.Combine(temporary.FullPath, "real-destination");
        Directory.Move(destination, targetOfLink);
        TryCreateDirectoryLinkOrSkip(destination, targetOfLink);

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        MoveLeaseAcquisition acquisition = await new WindowsPhysicalMoveAdapter().TryAcquireAsync(
            globalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionReadiness.Blocked, acquisition.Readiness);
        Assert.Equal(
            ExecutionProblemKind.DestinationRevalidationFailed,
            acquisition.Problem!.Kind);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(targetOfLink, "report.txt")));
    }

    [Fact]
    public async Task CommitTimeDestinationReparseObservationBlocksBeforeNativeCall()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "planned");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        CanonicalPath destinationPath = manifest.Items[0].DestinationDirectory;
        var realObserver = new WindowsPhysicalIdentityAdapter();
        int nativeCalls = 0;
        var adapter = new WindowsPhysicalMoveAdapter(
            isWindows: true,
            (path, token) => StringComparer.OrdinalIgnoreCase.Equals(
                path.Value,
                destinationPath.Value)
                    ? PhysicalEntryObservation.Unsupported(
                        PhysicalEntryObservationReason.ReparsePoint,
                        PhysicalEntryKind.ReparsePoint)
                    : realObserver.Observe(path, token),
            (_, _) =>
            {
                nativeCalls++;
                return WindowsRenameResult.Success;
            });

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        MoveLeaseAcquisition acquisition = await adapter.TryAcquireAsync(
            globalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);
        await using IMoveOperationLease lease = Assert.IsAssignableFrom<IMoveOperationLease>(
            acquisition.Lease);

        MoveAttemptResult attempt = await lease.CommitAsync();

        Assert.Equal(MoveAttemptOutcome.Blocked, attempt.Outcome);
        Assert.Equal(
            ExecutionProblemKind.DestinationRevalidationFailed,
            attempt.Problem!.Kind);
        Assert.Equal(0, nativeCalls);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(destination, "report.txt")));
    }

    [Fact]
    public async Task NativeRaceConflictIsPreservedAndCommitIsOneShot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "source");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        int nativeCalls = 0;
        var adapter = new WindowsPhysicalMoveAdapter(
            isWindows: true,
            new WindowsPhysicalIdentityAdapter().Observe,
            (_, _) =>
            {
                nativeCalls++;
                return new WindowsRenameResult(
                    Succeeded: false,
                    WindowsNoOverwriteRenameNative.ErrorAlreadyExists);
            });

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        MoveLeaseAcquisition acquisition = await adapter.TryAcquireAsync(
            globalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);
        await using IMoveOperationLease lease = Assert.IsAssignableFrom<IMoveOperationLease>(
            acquisition.Lease);

        MoveAttemptResult first = await lease.CommitAsync();
        MoveAttemptResult second = await lease.CommitAsync();

        Assert.Same(first, second);
        Assert.Equal(MoveAttemptOutcome.Conflict, first.Outcome);
        Assert.Equal(1, nativeCalls);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task ReportedSuccessWithFailedFreshInspectionIsAmbiguous()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "source");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        var realObserver = new WindowsPhysicalIdentityAdapter();
        bool nativeReturned = false;
        var adapter = new WindowsPhysicalMoveAdapter(
            isWindows: true,
            (path, token) => nativeReturned
                ? PhysicalEntryObservation.Inaccessible(
                    PhysicalEntryObservationReason.InputOutputFailure)
                : realObserver.Observe(path, token),
            (_, _) =>
            {
                nativeReturned = true;
                return WindowsRenameResult.Success;
            });

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        MoveLeaseAcquisition acquisition = await adapter.TryAcquireAsync(
            globalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);
        await using IMoveOperationLease lease = Assert.IsAssignableFrom<IMoveOperationLease>(
            acquisition.Lease);

        Assert.Equal(MoveAttemptOutcome.Succeeded, (await lease.CommitAsync()).Outcome);
        MoveObservation observation = await lease.ObserveAsync();
        RecoveryResolution resolution = MoveRecoveryStateMachine.Classify(
            lease.SourcePhysicalIdentity,
            observation.Source,
            observation.Target);

        Assert.Equal(RecoveryDisposition.Ambiguous, resolution.Disposition);
        Assert.Equal(RecoveryReasonKind.InspectionFailed, resolution.Reason);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task NativeFailureWithFreshUnchangedStateIsNotApplied()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "source");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        var adapter = new WindowsPhysicalMoveAdapter(
            isWindows: true,
            new WindowsPhysicalIdentityAdapter().Observe,
            (_, _) => new WindowsRenameResult(Succeeded: false, ErrorCode: 5));

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        MoveLeaseAcquisition acquisition = await adapter.TryAcquireAsync(
            globalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);
        await using IMoveOperationLease lease = Assert.IsAssignableFrom<IMoveOperationLease>(
            acquisition.Lease);

        Assert.Equal(MoveAttemptOutcome.Failed, (await lease.CommitAsync()).Outcome);
        MoveObservation observation = await lease.ObserveAsync();

        Assert.Equal(
            RecoveryDisposition.NotApplied,
            MoveRecoveryStateMachine.Classify(
                lease.SourcePhysicalIdentity,
                observation.Source,
                observation.Target).Disposition);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(destination, "report.txt")));
    }

    [Fact]
    public async Task AppliedManifestCannotSilentlyExecuteAgain()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "source");
        string target = Path.Combine(destination, "report.txt");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        var adapter = new WindowsPhysicalMoveAdapter();

        await using (IGlobalExecutionLease firstGlobalLease = await AcquireGlobalLease())
        {
            MoveLeaseAcquisition first = await adapter.TryAcquireAsync(
                firstGlobalLease,
                manifest,
                manifest.Items[0],
                TestContext.Current.CancellationToken);
            await using IMoveOperationLease firstLease =
                Assert.IsAssignableFrom<IMoveOperationLease>(first.Lease);
            Assert.Equal(MoveAttemptOutcome.Succeeded, (await firstLease.CommitAsync()).Outcome);
            MoveObservation observation = await firstLease.ObserveAsync();
            Assert.Equal(
                RecoveryDisposition.Applied,
                MoveRecoveryStateMachine.Classify(
                    firstLease.SourcePhysicalIdentity,
                    observation.Source,
                    observation.Target).Disposition);
        }

        await using IGlobalExecutionLease secondGlobalLease = await AcquireGlobalLease();
        MoveLeaseAcquisition second = await adapter.TryAcquireAsync(
            secondGlobalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionReadiness.Blocked, second.Readiness);
        Assert.Equal(ExecutionProblemKind.SourceRevalidationFailed, second.Problem!.Kind);
        Assert.False(File.Exists(source));
        Assert.Equal("source", File.ReadAllText(target));
    }

    [Fact]
    public async Task ForgedLeaseIsRejectedBeforeAnyPhysicalObservation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows capability test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "source");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        int observations = 0;
        var adapter = new WindowsPhysicalMoveAdapter(
            isWindows: true,
            (_, _) =>
            {
                observations++;
                return PhysicalEntryObservation.Missing(2);
            },
            (_, _) => throw new InvalidOperationException("Native call must not run."));

        await using var forged = new ForgedLease();
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await adapter.TryAcquireAsync(
                forged,
                manifest,
                manifest.Items[0],
                TestContext.Current.CancellationToken));
        Assert.Equal(0, observations);
    }

    [Fact]
    public async Task DisposedGenuineLeaseCannotAuthorizePhysicalAccess()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows capability test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "source");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        IGlobalExecutionLease disposed = await AcquireGlobalLease();
        await disposed.DisposeAsync();
        int observations = 0;
        var adapter = new WindowsPhysicalMoveAdapter(
            isWindows: true,
            (_, _) =>
            {
                observations++;
                return PhysicalEntryObservation.Missing(2);
            },
            (_, _) => throw new InvalidOperationException("Native call must not run."));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await adapter.TryAcquireAsync(
                disposed,
                manifest,
                manifest.Items[0],
                TestContext.Current.CancellationToken));

        Assert.Equal(0, observations);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task WholeManifestPreflightNeverInvokesTheNativeRename()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native preflight test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string second = temporary.WriteFile(Path.Combine("source", "zeta.txt"), "second");
        string first = temporary.WriteFile(Path.Combine("source", "alpha.txt"), "first");
        OrganizationPlan plan = WindowsPhysicalMoveTestPlan.CreatePlan(
            sourceRoot,
            [second, first],
            destination);
        ExecutionManifest manifest = Assert.IsType<ExecutionManifest>(
            ExecutionManifestCompiler.Compile(plan).Manifest);
        int nativeCalls = 0;
        var observer = new WindowsPhysicalIdentityAdapter();
        var adapter = new WindowsPhysicalMoveAdapter(
            isWindows: true,
            observer.Observe,
            (_, _) =>
            {
                nativeCalls++;
                return WindowsRenameResult.Success;
            });

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        ExecutionPreflightResult preflight = await adapter.InspectAsync(
            globalLease,
            manifest,
            TestContext.Current.CancellationToken);

        Assert.True(preflight.Succeeded);
        Assert.All(
            preflight.Items,
            static item => Assert.Equal(ExecutionReadiness.Ready, item.Readiness));
        Assert.Equal(0, nativeCalls);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.False(File.Exists(Path.Combine(destination, "alpha.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "zeta.txt")));
    }

    [Fact]
    public async Task RecoveryObservationRequiresGenuineLeaseAndNeverInvokesRename()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows recovery observation test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "source");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        int observations = 0;
        int nativeCalls = 0;
        var adapter = new WindowsPhysicalMoveAdapter(
            isWindows: true,
            (path, _) =>
            {
                observations++;
                return path.Equals(manifest.Items[0].SourcePrecondition.CanonicalPath)
                    ? PhysicalEntryObservation.Success(
                        manifest.Items[0].SourcePrecondition.PhysicalIdentity!,
                        new PhysicalFileMetadata(
                            manifest.Items[0].SourcePrecondition.Length,
                            manifest.Items[0].SourcePrecondition.CreatedUtc,
                            manifest.Items[0].SourcePrecondition.ModifiedUtc))
                    : PhysicalEntryObservation.Missing(2);
            },
            (_, _) =>
            {
                nativeCalls++;
                return WindowsRenameResult.Success;
            });

        await using var forged = new ForgedLease();
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await adapter.ObserveAsync(
                forged,
                manifest.Items[0],
                TestContext.Current.CancellationToken));
        Assert.Equal(0, observations);

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        MoveObservation observation = await adapter.ObserveAsync(
            globalLease,
            manifest.Items[0],
            TestContext.Current.CancellationToken);

        Assert.Equal(RecoveryEntryObservationKind.Present, observation.Source.Kind);
        Assert.Equal(
            manifest.Items[0].SourcePrecondition.PhysicalIdentity,
            observation.Source.Identity);
        Assert.Equal(RecoveryEntryObservationKind.Missing, observation.Target.Kind);
        Assert.Equal(2, observations);
        Assert.Equal(0, nativeCalls);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(manifest.Items[0].TargetPath.Value));
    }

    [Fact]
    public async Task MissingParentIsNotMistakenForSafeTargetAbsence()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "source");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        var adapter = new WindowsPhysicalMoveAdapter(
            isWindows: true,
            (_, _) => PhysicalEntryObservation.Missing(3),
            (_, _) => throw new InvalidOperationException("Native call must not run."));

        await using IGlobalExecutionLease globalLease = await AcquireGlobalLease();
        MoveLeaseAcquisition acquisition = await adapter.TryAcquireAsync(
            globalLease,
            manifest,
            manifest.Items[0],
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionReadiness.Blocked, acquisition.Readiness);
        Assert.Equal(
            ExecutionProblemKind.DestinationRevalidationFailed,
            acquisition.Problem!.Kind);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void PhysicalVolumeMismatchIsStructuredBlockedBeforeTheNativeBoundary()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native move test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "source");
        ExecutionManifest manifest = WindowsPhysicalMoveTestPlan.Create(
            sourceRoot,
            source,
            destination);
        ExecutionManifestItem item = manifest.Items[0];
        var differentVolume = new PhysicalEntryIdentity(
            PhysicalEntryIdentity.WindowsFileIdScheme,
            PhysicalEntryIdentity.WindowsFileIdSchemeVersion,
            "different-volume",
            "destination",
            PhysicalEntryKind.Directory);

        MoveLeaseAcquisition? failure = WindowsPhysicalMoveAdapter.ValidatePhysicalVolumes(
            item,
            item.SourcePrecondition.PhysicalIdentity!,
            manifest.SourceRootPhysicalIdentity,
            differentVolume);

        Assert.NotNull(failure);
        Assert.Equal(ExecutionReadiness.Blocked, failure.Readiness);
        Assert.Equal(
            ExecutionProblemKind.DestinationRevalidationFailed,
            failure.Problem!.Kind);
    }

    private static async Task<IGlobalExecutionLease> AcquireGlobalLease()
    {
        GlobalExecutionLeaseAcquisition acquisition =
            await new WindowsGlobalExecutionLeaseProvider().AcquireAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Acquired, acquisition.Status);
        return Assert.IsAssignableFrom<IGlobalExecutionLease>(acquisition.Lease);
    }

    private static void TryCreateFileLinkOrSkip(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip(
                $"Symbolic links are unavailable in this test environment: {exception.GetType().Name}");
        }
    }

    private static void TryCreateDirectoryLinkOrSkip(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip(
                $"Symbolic links are unavailable in this test environment: {exception.GetType().Name}");
        }
    }

    private sealed class ForgedLease : IGlobalExecutionLease
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
