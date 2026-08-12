using System.Diagnostics;

namespace FileOrganizer.Infrastructure.Tests.Journaling;

[Collection("Windows global execution lease")]
public sealed class WindowsGlobalExecutionLeaseProcessTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task LeaseBlocksAnotherProcessUntilNormalRelease()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This is a Windows process integration test.");

        using Process owner = StartProbe("hold");
        try
        {
            Assert.StartsWith("READY|", await ReadLineAsync(owner));

            ProcessResult blocked = await RunProbeAsync("try");
            Assert.Equal(0, blocked.ExitCode);
            Assert.Equal("RESULT|Busy|False", blocked.StandardOutput);
            Assert.Empty(blocked.StandardError);

            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await owner.StandardInput.WriteLineAsync("release".AsMemory(), cancellationToken);
            await owner.StandardInput.FlushAsync(cancellationToken);
            await WaitForExitAsync(owner);

            Assert.Equal(0, owner.ExitCode);
            string? released = await owner.StandardOutput.ReadLineAsync(
                cancellationToken);
            Assert.Equal("RELEASED", released);
            Assert.Empty(await owner.StandardError.ReadToEndAsync(cancellationToken));

            ProcessResult afterRelease = await RunProbeAsync("try");
            Assert.Equal(0, afterRelease.ExitCode);
            Assert.StartsWith("RESULT|Acquired|", afterRelease.StandardOutput);
            Assert.Empty(afterRelease.StandardError);
        }
        finally
        {
            await TerminateIfRunningAsync(owner);
        }
    }

    [Fact]
    public async Task LeaseIsReleasedWhenOwningProcessIsTerminated()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This is a Windows process integration test.");

        using Process owner = StartProbe("hold");
        try
        {
            Assert.StartsWith("READY|", await ReadLineAsync(owner));

            owner.Kill(entireProcessTree: true);
            await WaitForExitAsync(owner);

            ProcessResult afterTermination = await RunProbeAsync("try");
            Assert.Equal(0, afterTermination.ExitCode);
            Assert.StartsWith("RESULT|Acquired|", afterTermination.StandardOutput);
            Assert.Empty(afterTermination.StandardError);
        }
        finally
        {
            await TerminateIfRunningAsync(owner);
        }
    }

    private static async Task<ProcessResult> RunProbeAsync(string command)
    {
        using Process process = StartProbe(command);
        try
        {
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await WaitForExitAsync(process);

            return new ProcessResult(
                process.ExitCode,
                (await standardOutput).TrimEnd('\r', '\n'),
                (await standardError).TrimEnd('\r', '\n'));
        }
        finally
        {
            await TerminateIfRunningAsync(process);
        }
    }

    private static Process StartProbe(string command)
    {
        string executablePath = Path.Combine(
            AppContext.BaseDirectory,
            "LeaseProbe",
            "FileOrganizer.Infrastructure.LeaseProbe.exe");

        Assert.True(
            File.Exists(executablePath),
            $"Lease probe executable was not copied to the test output: {executablePath}");

        var startInfo = new ProcessStartInfo(executablePath, command)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The lease probe process could not be started.");
    }

    private static async Task<string> ReadLineAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        try
        {
            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is not null)
            {
                return line;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The lease probe did not signal readiness before the deadline.");
        }

        string error = await process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        throw new InvalidOperationException(
            $"The lease probe ended before emitting its protocol line. Standard error: {error}");
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
            throw new TimeoutException("The lease probe did not exit before the deadline.");
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

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
