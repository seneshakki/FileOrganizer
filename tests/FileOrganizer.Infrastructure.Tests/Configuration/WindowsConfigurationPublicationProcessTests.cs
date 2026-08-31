using System.Diagnostics;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Infrastructure.Configuration;

namespace FileOrganizer.Infrastructure.Tests.Configuration;

[Collection(WindowsConfigurationPublicationTestGroup.Name)]
public sealed class WindowsConfigurationPublicationProcessTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task NamedPublicationGateSerializesASeparateProcess()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows process integration test.");
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = Path.Combine(temp.FullPath, "configuration.json");
        using Process owner = StartProbe(
            "hold-named-mutex",
            WindowsConfigurationPublicationGate.MutexName);
        try
        {
            Assert.Equal("READY|NamedMutex", await ReadLineAsync(owner));
            FileConfigurationTextStore blockedStore = CreateStore(TimeSpan.Zero);

            ConfigurationTextWriteResult blocked = await blockedStore.WriteAsync(
                Request(path, "blocked"),
                TestContext.Current.CancellationToken);

            Assert.Equal(ConfigurationTextWriteStatus.Failed, blocked.Status);
            Assert.Equal(
                ConfigurationStorageProblemKind.PublicationSynchronizationUnavailable,
                Assert.Single(blocked.Problems).Kind);
            Assert.False(File.Exists(path));

            CancellationToken token = TestContext.Current.CancellationToken;
            await owner.StandardInput.WriteLineAsync("release".AsMemory(), token);
            await owner.StandardInput.FlushAsync(token);
            await WaitForExitAsync(owner);
            Assert.Equal(0, owner.ExitCode);
            Assert.Equal("RELEASED", await owner.StandardOutput.ReadLineAsync(token));
            Assert.Empty(await owner.StandardError.ReadToEndAsync(token));

            ConfigurationTextWriteResult afterRelease = await CreateStore(
                    TimeSpan.FromSeconds(5))
                .WriteAsync(Request(path, "published"), token);
            Assert.Equal(ConfigurationTextWriteStatus.Succeeded, afterRelease.Status);
            Assert.Equal("published", File.ReadAllText(path));
        }
        finally
        {
            await TerminateIfRunningAsync(owner);
        }
    }

    private static FileConfigurationTextStore CreateStore(TimeSpan timeout) =>
        new(
            new DurableConfigurationFilePublisher(),
            new WindowsConfigurationPathIdentityComparer(),
            new WindowsConfigurationPublicationGate(timeout),
            new ConfigurationPathGateRegistry());

    private static ConfigurationTextWriteRequest Request(string path, string content) =>
        new(path, content, ConfigurationWritePrecondition.Unconditional);

    private static Process StartProbe(params string[] arguments)
    {
        string executablePath = Path.Combine(
            AppContext.BaseDirectory,
            "LeaseProbe",
            "FileOrganizer.Infrastructure.LeaseProbe.exe");
        Assert.True(File.Exists(executablePath));
        var startInfo = new ProcessStartInfo(executablePath)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The lease probe process could not start.");
    }

    private static async Task<string> ReadLineAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        try
        {
            return await process.StandardOutput.ReadLineAsync(timeout.Token)
                ?? throw new InvalidOperationException("The lease probe exited without output.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The lease probe did not become ready.");
        }
    }

    private static async Task WaitForExitAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The lease probe did not exit.");
        }
    }

    private static async Task TerminateIfRunningAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        await WaitForExitAsync(process);
    }
}
