using FileOrganizer.Infrastructure.Configuration;

namespace FileOrganizer.Infrastructure.Tests.Configuration;

public sealed class ConfigurationPathGateRegistryTests
{
    [Fact]
    public async Task DriveAndExtendedPathAliasesShareOneSameProcessGate()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path-alias test.");
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        string driveKey = ConfigurationPublicationPath.CreateCanonicalKey(path);
        string extendedKey = ConfigurationPublicationPath.CreateCanonicalKey(@"\\?\" + path);
        var registry = new ConfigurationPathGateRegistry();
        await using IAsyncDisposable first = await registry.AcquireAsync(
            driveKey,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Task<IAsyncDisposable> second = registry.AcquireAsync(
            extendedKey,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken).AsTask();

        Assert.Equal(driveKey, extendedKey);
        Assert.False(second.IsCompleted);
        await first.DisposeAsync();
        await using IAsyncDisposable secondLease = await second;
    }

    [Fact]
    public async Task ReleasedPathGatesAreEvictedInsteadOfAccumulatingForever()
    {
        var registry = new ConfigurationPathGateRegistry();

        for (int index = 0; index < 100; index++)
        {
            await using IAsyncDisposable lease = await registry.AcquireAsync(
                $"configuration-{index}",
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, registry.ActiveGateCount);
    }

    [Fact]
    public async Task CanceledWaiterDoesNotLeakOrRetireAnOwnedGate()
    {
        var registry = new ConfigurationPathGateRegistry();
        await using IAsyncDisposable owner = await registry.AcquireAsync(
            "configuration",
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        Task<IAsyncDisposable> waiter = registry.AcquireAsync(
            "configuration",
            TimeSpan.FromSeconds(10),
            cancellation.Token).AsTask();

        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
        Assert.Equal(1, registry.ActiveGateCount);
        await owner.DisposeAsync();
        Assert.Equal(0, registry.ActiveGateCount);
    }

    [Fact]
    public async Task ConcurrentCancellationAndReleaseChurnLeavesNoRetiredEntryOrWaiter()
    {
        var registry = new ConfigurationPathGateRegistry();
        IAsyncDisposable owner = await registry.AcquireAsync(
            "contended-configuration",
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        using var canceledWaiters = new CancellationTokenSource();
        Task<IAsyncDisposable>[] canceled = Enumerable.Range(0, 32)
            .Select(
                _ => registry.AcquireAsync(
                    "contended-configuration",
                    TimeSpan.FromSeconds(10),
                    canceledWaiters.Token).AsTask())
            .ToArray();
        Task<IAsyncDisposable>[] survivors = Enumerable.Range(0, 32)
            .Select(
                _ => registry.AcquireAsync(
                    "contended-configuration",
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken).AsTask())
            .ToArray();

        await canceledWaiters.CancelAsync();
        foreach (Task<IAsyncDisposable> canceledWaiter in canceled)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => canceledWaiter);
        }

        await owner.DisposeAsync();
        foreach (Task<IAsyncDisposable> survivor in survivors)
        {
            await using IAsyncDisposable lease = await survivor;
        }

        Assert.Equal(0, registry.ActiveGateCount);
    }
}
