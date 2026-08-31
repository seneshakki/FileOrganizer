namespace FileOrganizer.Infrastructure.Tests.Configuration;

internal sealed class BoundedNamedMutexOwner : IDisposable
{
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(10);
    private readonly string _mutexName;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _thread;
    private int _acquired;
    private int _started;

    internal BoundedNamedMutexOwner(string mutexName)
    {
        _mutexName = mutexName;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Configuration publication test owner",
        };
    }

    internal void StartAndWait()
    {
        _thread.Start();
        Interlocked.Exchange(ref _started, 1);
        if (!_ready.Wait(HelperTimeout))
        {
            _release.Set();
            _thread.Join(HelperTimeout);
            throw new TimeoutException("The test mutex owner did not start.");
        }

        if (Volatile.Read(ref _acquired) == 0)
        {
            _thread.Join(HelperTimeout);
            throw new TimeoutException("The test mutex owner could not acquire the mutex.");
        }
    }

    public void Dispose()
    {
        _release.Set();
        bool stopped = Volatile.Read(ref _started) == 0
            || _thread.Join(HelperTimeout);
        if (stopped)
        {
            _ready.Dispose();
            _release.Dispose();
        }
    }

    private void Run()
    {
        using var mutex = new Mutex(initiallyOwned: false, _mutexName);
        bool ownsMutex = false;
        try
        {
            try
            {
                int signaled = WaitHandle.WaitAny(
                    [mutex, _release.WaitHandle],
                    HelperTimeout);
                ownsMutex = signaled == 0;
            }
            catch (AbandonedMutexException exception) when (exception.MutexIndex == 0)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                _ready.Set();
                return;
            }

            Interlocked.Exchange(ref _acquired, 1);
            _ready.Set();
            _release.Wait();
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
