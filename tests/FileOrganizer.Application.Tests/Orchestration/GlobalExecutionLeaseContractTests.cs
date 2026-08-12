using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Application.Tests.Orchestration;

public sealed class GlobalExecutionLeaseContractTests
{
    [Fact]
    public async Task AcquisitionResultsExposeCapabilityOnlyForAcquiredStatus()
    {
        TestLease lease = new();

        GlobalExecutionLeaseAcquisition acquired =
            GlobalExecutionLeaseAcquisition.Acquired(
                lease,
                previousOwnerAbandoned: true);
        GlobalExecutionLeaseAcquisition busy = GlobalExecutionLeaseAcquisition.Busy();
        GlobalExecutionLeaseAcquisition unsupported =
            GlobalExecutionLeaseAcquisition.Unsupported();
        GlobalExecutionLeaseAcquisition failed = GlobalExecutionLeaseAcquisition.Failed(
            GlobalExecutionLeaseFailureKind.AccessDenied);

        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Acquired, acquired.Status);
        Assert.Same(lease, acquired.Lease);
        Assert.True(acquired.PreviousOwnerAbandoned);
        Assert.Null(acquired.FailureKind);

        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Busy, busy.Status);
        Assert.Null(busy.Lease);
        Assert.Null(busy.FailureKind);

        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Unsupported, unsupported.Status);
        Assert.Null(unsupported.Lease);
        Assert.Equal(
            GlobalExecutionLeaseFailureKind.UnsupportedPlatform,
            unsupported.FailureKind);

        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Failed, failed.Status);
        Assert.Null(failed.Lease);
        Assert.Equal(GlobalExecutionLeaseFailureKind.AccessDenied, failed.FailureKind);

        await acquired.Lease!.DisposeAsync();
    }

    [Fact]
    public void FailedFactoryRejectsTheUnsupportedCapabilityReason()
    {
        Assert.Throws<ArgumentException>(
            () => GlobalExecutionLeaseAcquisition.Failed(
                GlobalExecutionLeaseFailureKind.UnsupportedPlatform));
    }

    [Fact]
    public async Task CancellationAfterAcquisitionDoesNotRevokeTheCapability()
    {
        FakeGlobalExecutionLeaseProvider provider = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        GlobalExecutionLeaseAcquisition acquired = await provider.AcquireAsync(
            TimeSpan.Zero,
            cancellation.Token);

        cancellation.Cancel();
        GlobalExecutionLeaseAcquisition second = await provider.AcquireAsync(
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Acquired, acquired.Status);
        Assert.True(provider.IsOwned);
        Assert.Equal(GlobalExecutionLeaseAcquisitionStatus.Busy, second.Status);

        await acquired.Lease!.DisposeAsync();
        Assert.False(provider.IsOwned);
    }

    [Fact]
    public async Task CancellationBeforeAcquisitionPropagatesWithoutOwnership()
    {
        FakeGlobalExecutionLeaseProvider provider = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.AcquireAsync(TimeSpan.Zero, cancellation.Token).AsTask());

        Assert.Equal(0, provider.AcquisitionCount);
        Assert.False(provider.IsOwned);
    }

    private sealed class TestLease : IGlobalExecutionLease
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
