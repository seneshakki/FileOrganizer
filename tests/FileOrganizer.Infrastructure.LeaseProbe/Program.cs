using FileOrganizer.Application.Journaling;
using FileOrganizer.Infrastructure.Journaling;

namespace FileOrganizer.Infrastructure.LeaseProbe;

internal static class Program
{
    private const string HoldCommand = "hold";
    private const string HoldNamedMutexCommand = "hold-named-mutex";
    private const string TryCommand = "try";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == HoldNamedMutexCommand)
        {
            return HoldNamedMutex(args[1]);
        }

        if (args.Length != 1 || args[0] is not (HoldCommand or TryCommand))
        {
            await WriteLineAsync("ERROR|InvalidCommand");
            return 64;
        }

        var provider = new WindowsGlobalExecutionLeaseProvider();
        TimeSpan timeout = args[0] == TryCommand
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(5);
        GlobalExecutionLeaseAcquisition acquisition = await provider.AcquireAsync(
            timeout,
            CancellationToken.None);

        if (args[0] == TryCommand)
        {
            await WriteLineAsync(
                $"RESULT|{acquisition.Status}|{acquisition.PreviousOwnerAbandoned}");

            if (acquisition.Lease is not null)
            {
                await acquisition.Lease.DisposeAsync();
            }

            return 0;
        }

        if (acquisition.Status != GlobalExecutionLeaseAcquisitionStatus.Acquired
            || acquisition.Lease is null)
        {
            await WriteLineAsync($"ERROR|{acquisition.Status}|{acquisition.FailureKind}");
            return 2;
        }

        await using (acquisition.Lease)
        {
            await WriteLineAsync($"READY|{acquisition.PreviousOwnerAbandoned}");
            string? command = await Console.In.ReadLineAsync();
            if (!string.Equals(command, "release", StringComparison.Ordinal))
            {
                return 3;
            }
        }

        await WriteLineAsync("RELEASED");
        return 0;
    }

    private static int HoldNamedMutex(string mutexName)
    {
        if (string.IsNullOrWhiteSpace(mutexName))
        {
            Console.Out.WriteLine("ERROR|InvalidMutexName");
            Console.Out.Flush();
            return 64;
        }

        using var mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
        }

        try
        {
            Console.Out.WriteLine("READY|NamedMutex");
            Console.Out.Flush();
            string? command = Console.In.ReadLine();
            if (!string.Equals(command, "release", StringComparison.Ordinal))
            {
                return 3;
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        Console.Out.WriteLine("RELEASED");
        Console.Out.Flush();
        return 0;
    }

    private static async Task WriteLineAsync(string message)
    {
        await Console.Out.WriteLineAsync(message);
        await Console.Out.FlushAsync();
    }
}
