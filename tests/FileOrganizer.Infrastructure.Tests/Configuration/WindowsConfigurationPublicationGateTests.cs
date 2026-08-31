using FileOrganizer.Application.Configuration;
using FileOrganizer.Infrastructure.Configuration;

namespace FileOrganizer.Infrastructure.Tests.Configuration;

[Collection(WindowsConfigurationPublicationTestGroup.Name)]
public sealed class WindowsConfigurationPublicationGateTests
{
    [Fact]
    public async Task DifferentPathFanOutCreatesOnlyOneOsOwnerWorker()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named mutex test.");
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        using var externalOwner = new BoundedNamedMutexOwner(
            WindowsConfigurationPublicationGate.MutexName);
        externalOwner.StartAndWait();
        using var cancellation = new CancellationTokenSource();
        Task<ConfigurationTextWriteResult>[] writes = Enumerable.Range(0, 32)
            .Select(
                index => CreateStore(TimeSpan.FromSeconds(10)).WriteAsync(
                    Request(Path.Combine(temp.FullPath, $"configuration-{index}.json")),
                    cancellation.Token).AsTask())
            .ToArray();

        using var observationTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        observationTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (WindowsConfigurationPublicationGate.ActiveOwnerWorkerCount == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), observationTimeout.Token);
        }

        Assert.Equal(1, WindowsConfigurationPublicationGate.ActiveOwnerWorkerCount);
        await cancellation.CancelAsync();

        ConfigurationTextWriteResult[] results = await Task.WhenAll(writes);
        Assert.All(
            results,
            result =>
            {
                Assert.Equal(ConfigurationTextWriteStatus.Failed, result.Status);
                Assert.Equal(
                    ConfigurationStorageProblemKind.PublicationSynchronizationCanceled,
                    Assert.Single(result.Problems).Kind);
            });
        Assert.Equal(0, WindowsConfigurationPublicationGate.ActiveOwnerWorkerCount);
        Assert.Empty(Directory.GetFiles(temp.FullPath));
    }

    [Fact]
    public async Task ConcurrentDisposeCallsShareTheSameReleaseCompletion()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows named mutex test.");
        var gate = new WindowsConfigurationPublicationGate(TimeSpan.FromSeconds(5));
        IConfigurationPublicationLease lease = await gate.AcquireAsync(
            "configuration",
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        ValueTask firstDispose = lease.DisposeAsync();
        ValueTask secondDispose = lease.DisposeAsync();
        Task firstCompletion = firstDispose.AsTask();
        Task secondCompletion = secondDispose.AsTask();

        Assert.Same(firstCompletion, secondCompletion);
        await Task.WhenAll(firstCompletion, secondCompletion);
        Assert.Equal(0, WindowsConfigurationPublicationGate.ActiveOwnerWorkerCount);

        await using IConfigurationPublicationLease reacquired = await gate.AcquireAsync(
            "configuration",
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
    }

    private static FileConfigurationTextStore CreateStore(TimeSpan timeout) =>
        new(
            new DurableConfigurationFilePublisher(),
            new WindowsConfigurationPathIdentityComparer(),
            new WindowsConfigurationPublicationGate(timeout),
            new ConfigurationPathGateRegistry());

    private static ConfigurationTextWriteRequest Request(string path) =>
        new(path, "draft", ConfigurationWritePrecondition.Unconditional);
}
