using System.Diagnostics;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Infrastructure.Journaling;

namespace FileOrganizer.Infrastructure.Tests.Journaling;

[Collection(WindowsGlobalExecutionLeaseTestGroup.Name)]
public sealed class WindowsGlobalExecutionLeaseProviderTests
{
    [Fact]
    public async Task AcquireAsyncWhileOwnedReturnsBusyThenAcquiresAfterRelease()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named mutex test.");

        var firstProvider = new WindowsGlobalExecutionLeaseProvider();
        var secondProvider = new WindowsGlobalExecutionLeaseProvider();
        GlobalExecutionLeaseAcquisition first = await firstProvider.AcquireAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        await using IGlobalExecutionLease firstLease =
            Assert.IsAssignableFrom<IGlobalExecutionLease>(first.Lease);

        GlobalExecutionLeaseAcquisition blocked = await secondProvider.AcquireAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Acquired, first.Status);
        Assert.False(first.PreviousOwnerAbandoned);
        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Busy, blocked.Status);
        Assert.Null(blocked.Lease);

        await firstLease.DisposeAsync();

        GlobalExecutionLeaseAcquisition afterRelease = await secondProvider.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Acquired, afterRelease.Status);
        await Assert.IsAssignableFrom<IGlobalExecutionLease>(afterRelease.Lease).DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsyncBoundedWaitReturnsBusyWithoutWaitingForever()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named mutex test.");

        var provider = new WindowsGlobalExecutionLeaseProvider();
        GlobalExecutionLeaseAcquisition first = await provider.AcquireAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        await using IGlobalExecutionLease lease =
            Assert.IsAssignableFrom<IGlobalExecutionLease>(first.Lease);
        var timer = Stopwatch.StartNew();

        GlobalExecutionLeaseAcquisition blocked = await provider.AcquireAsync(
            TimeSpan.FromMilliseconds(75),
            TestContext.Current.CancellationToken);

        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Busy, blocked.Status);
        Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposeAsyncIsIdempotentAndReleasesFromAsyncCallerThread()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named mutex test.");

        var provider = new WindowsGlobalExecutionLeaseProvider();
        GlobalExecutionLeaseAcquisition acquisition = await provider.AcquireAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        await using IGlobalExecutionLease lease =
            Assert.IsAssignableFrom<IGlobalExecutionLease>(acquisition.Lease);

        await Task.Run(
            async () => await lease.DisposeAsync(),
            TestContext.Current.CancellationToken);
        await lease.DisposeAsync();

        GlobalExecutionLeaseAcquisition reacquired = await provider.AcquireAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Acquired, reacquired.Status);
        await Assert.IsAssignableFrom<IGlobalExecutionLease>(reacquired.Lease).DisposeAsync();
    }

    [Fact]
    public async Task BorrowPinsMutexUntilBorrowAndPublicLeaseAreDisposed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named mutex test.");

        var provider = new WindowsGlobalExecutionLeaseProvider();
        GlobalExecutionLeaseAcquisition acquisition = await provider.AcquireAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        IGlobalExecutionLease lease =
            Assert.IsAssignableFrom<IGlobalExecutionLease>(acquisition.Lease);
        IWindowsExecutionLeaseBorrow? borrow = null;
        try
        {
            Assert.True(WindowsGlobalExecutionLeaseProvider.TryBorrow(lease, out borrow));
            Assert.NotNull(borrow);

            Task disposal = lease.DisposeAsync().AsTask();

            Assert.False(disposal.IsCompleted);
            Assert.False(WindowsGlobalExecutionLeaseProvider.TryBorrow(lease, out var afterDispose));
            Assert.Null(afterDispose);

            GlobalExecutionLeaseAcquisition blocked = await provider.AcquireAsync(
                TimeSpan.Zero,
                TestContext.Current.CancellationToken);
            Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Busy, blocked.Status);

            borrow.Dispose();
            borrow.Dispose();
            await disposal.WaitAsync(TestContext.Current.CancellationToken);

            GlobalExecutionLeaseAcquisition reacquired = await provider.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken);
            Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Acquired, reacquired.Status);
            await Assert.IsAssignableFrom<IGlobalExecutionLease>(reacquired.Lease).DisposeAsync();
        }
        finally
        {
            borrow?.Dispose();
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryBorrowRejectsForeignAndNullLeaseCapabilities()
    {
        await using var foreignLease = new ForeignExecutionLease();

        Assert.False(WindowsGlobalExecutionLeaseProvider.TryBorrow(foreignLease, out var foreign));
        Assert.Null(foreign);
        Assert.False(WindowsGlobalExecutionLeaseProvider.TryBorrow(null, out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public async Task AcquireAsyncAfterAbandonedOwnerReportsMetadataAndRetainsOwnership()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named mutex test.");

        using var observer = new Mutex(
            initiallyOwned: false,
            WindowsGlobalExecutionLeaseProvider.MutexName);
        using var ownerReady = new ManualResetEventSlim(initialState: false);
        var owner = new Thread(() =>
        {
            using var abandoned = new Mutex(
                initiallyOwned: false,
                WindowsGlobalExecutionLeaseProvider.MutexName);
            abandoned.WaitOne();
            ownerReady.Set();
        });
        owner.Start();
        ownerReady.Wait(TestContext.Current.CancellationToken);
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)), "Abandoning owner thread did not exit.");

        var provider = new WindowsGlobalExecutionLeaseProvider();
        GlobalExecutionLeaseAcquisition acquisition = await provider.AcquireAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Acquired, acquisition.Status);
        Assert.True(acquisition.PreviousOwnerAbandoned);
        await using IGlobalExecutionLease lease =
            Assert.IsAssignableFrom<IGlobalExecutionLease>(acquisition.Lease);

        GlobalExecutionLeaseAcquisition blocked = await provider.AcquireAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Busy, blocked.Status);

        await lease.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsyncWhenNameBelongsToIncompatibleKernelObjectReturnsStructuredFailure()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named kernel-object test.");

        using var incompatibleObject = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            WindowsGlobalExecutionLeaseProvider.MutexName);
        var provider = new WindowsGlobalExecutionLeaseProvider();

        GlobalExecutionLeaseAcquisition result = await provider.AcquireAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Failed, result.Status);
        Assert.Equal(
            GlobalExecutionLeaseFailureKind.SynchronizationFailure,
            result.FailureKind);
        Assert.Null(result.Lease);
    }

    [Fact]
    public async Task AcquireAsyncPreCanceledThrowsOperationCanceledException()
    {
        var provider = new WindowsGlobalExecutionLeaseProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.AcquireAsync(TimeSpan.Zero, cancellation.Token));
    }

    [Fact]
    public async Task AcquireAsyncCancellationDuringBoundedWaitThrowsOperationCanceledException()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named mutex test.");

        var provider = new WindowsGlobalExecutionLeaseProvider();
        GlobalExecutionLeaseAcquisition first = await provider.AcquireAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        await using IGlobalExecutionLease lease =
            Assert.IsAssignableFrom<IGlobalExecutionLease>(first.Lease);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.AcquireAsync(TimeSpan.FromSeconds(5), cancellation.Token));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-1)]
    public async Task AcquireAsyncNegativeOrInfiniteTimeoutThrows(int milliseconds)
    {
        var provider = new WindowsGlobalExecutionLeaseProvider();
        TimeSpan timeout = TimeSpan.FromMilliseconds(milliseconds);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await provider.AcquireAsync(timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcquireAsyncNonWindowsReturnsStructuredUnsupportedCapability()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Non-Windows capability test.");
        var provider = new WindowsGlobalExecutionLeaseProvider();

        GlobalExecutionLeaseAcquisition result = await provider.AcquireAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Unsupported, result.Status);
        Assert.Equal(GlobalExecutionLeaseFailureKind.UnsupportedPlatform, result.FailureKind);
        Assert.Null(result.Lease);
    }

    private sealed class ForeignExecutionLease : IGlobalExecutionLease
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
