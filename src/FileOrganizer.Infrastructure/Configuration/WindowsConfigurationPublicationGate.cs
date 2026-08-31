using System.Diagnostics;

namespace FileOrganizer.Infrastructure.Configuration;

/// <summary>
/// Holds one Windows named mutex while a configuration publication performs
/// validation and its final atomic rename.
/// </summary>
/// <remarks>
/// The documented <see cref="Mutex"/> primitive provides process-crash
/// abandonment and current-session cross-process serialization. A dedicated
/// owner thread is required because Windows mutex ownership is thread-affine.
/// One fixed name deliberately serializes all participating configuration
/// writers regardless of drive, extended-path, UNC, 8.3, or hard-link aliases.
/// </remarks>
internal sealed class WindowsConfigurationPublicationGate : IConfigurationPublicationGate
{
    internal const string MutexName = @"Local\FileOrganizer.ConfigurationPublication.v1";
    private static readonly TimeSpan DefaultAcquisitionTimeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim ProcessPublicationGate = new(1, 1);
    private static int _activeOwnerWorkerCount;
    private readonly TimeSpan _acquisitionTimeout;

    internal WindowsConfigurationPublicationGate()
        : this(DefaultAcquisitionTimeout)
    {
    }

    internal WindowsConfigurationPublicationGate(TimeSpan acquisitionTimeout)
    {
        if (acquisitionTimeout < TimeSpan.Zero
            || acquisitionTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(acquisitionTimeout),
                "The publication gate timeout must be finite and non-negative.");
        }

        _acquisitionTimeout = acquisitionTimeout;
    }

    public TimeSpan AcquisitionTimeout => _acquisitionTimeout;

    internal static int ActiveOwnerWorkerCount => Volatile.Read(ref _activeOwnerWorkerCount);

    public async ValueTask<IConfigurationPublicationLease> AcquireAsync(
        string canonicalPathKey,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPathKey);
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return NoOpLease.Instance;
        }

        long startedTimestamp = Stopwatch.GetTimestamp();
        bool processGateOwned = false;
        IConfigurationPublicationLease? mutexLease = null;
        try
        {
            bool acquired = await ProcessPublicationGate.WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            if (!acquired)
            {
                throw SynchronizationTimeout(
                    "Timed out waiting for this process's configuration publication owner.");
            }

            processGateOwned = true;
            TimeSpan remaining = RemainingTimeout(startedTimestamp, timeout);
            mutexLease = await AcquireMutexAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
            return new ProcessSerializedLease(mutexLease);
        }
        catch
        {
            if (mutexLease is not null)
            {
                await mutexLease.DisposeAsync().ConfigureAwait(false);
            }

            if (processGateOwned)
            {
                ProcessPublicationGate.Release();
            }

            throw;
        }
    }

    private static async ValueTask<IConfigurationPublicationLease> AcquireMutexAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var worker = new MutexOwnerWorker(
            timeout,
            cancellationToken);
        try
        {
            worker.Start();
        }
        catch (Exception exception) when (exception is ThreadStateException
                                              or OutOfMemoryException)
        {
            worker.DisposeBeforeStart();
            throw new ConfigurationPublicationGateException(
                "The configuration publication synchronization worker could not start.",
                exception);
        }

        return await worker.AcquisitionTask.ConfigureAwait(false);
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The publication gate timeout must be finite and non-negative.");
        }
    }

    private static TimeSpan RemainingTimeout(long startedTimestamp, TimeSpan timeout)
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
        return elapsed >= timeout ? TimeSpan.Zero : timeout - elapsed;
    }

    private static ConfigurationPublicationGateException SynchronizationTimeout(
        string message) =>
        new(message, new TimeoutException(message));

    private static bool IsSynchronizationFailure(Exception exception) => exception is
        UnauthorizedAccessException
        or IOException
        or WaitHandleCannotBeOpenedException
        or InvalidOperationException
        or ObjectDisposedException
        or ApplicationException
        or TimeoutException;

    private enum WaitOutcome
    {
        Acquired,
        Abandoned,
    }

    private sealed class MutexOwnerWorker : IDisposable
    {
        private readonly TaskCompletionSource<IConfigurationPublicationLease> _acquisition =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationToken _cancellationToken;
        private readonly EventWaitHandle _releaseSignal = new(
            initialState: false,
            EventResetMode.ManualReset);
        private readonly TaskCompletionSource _releaseCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private readonly TimeSpan _timeout;
        private int _releaseRequested;

        internal MutexOwnerWorker(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            _timeout = timeout;
            _cancellationToken = cancellationToken;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "FileOrganizer configuration publication owner",
            };
        }

        internal Task<IConfigurationPublicationLease> AcquisitionTask => _acquisition.Task;

        internal Task ReleaseCompletion => _releaseCompletion.Task;

        internal void Start() => _thread.Start();

        public void Dispose() => RequestRelease();

        internal void DisposeBeforeStart()
        {
            Interlocked.Exchange(ref _releaseRequested, 1);
            _releaseSignal.Dispose();
            _releaseCompletion.TrySetResult();
        }

        internal void RequestRelease()
        {
            if (Interlocked.Exchange(ref _releaseRequested, 1) == 0)
            {
                _releaseSignal.Set();
            }
        }

        private void Run()
        {
            Interlocked.Increment(ref _activeOwnerWorkerCount);
            Mutex? mutex = null;
            bool ownsMutex = false;
            try
            {
                WaitOutcome outcome;
                try
                {
                    mutex = new Mutex(initiallyOwned: false, MutexName);
                    outcome = WaitForOwnership(mutex);
                    ownsMutex = true;
                }
                catch (OperationCanceledException)
                {
                    _acquisition.TrySetCanceled(_cancellationToken);
                    return;
                }
                catch (Exception exception) when (IsSynchronizationFailure(exception))
                {
                    _acquisition.TrySetException(
                        new ConfigurationPublicationGateException(
                            "The configuration publication mutex could not be acquired.",
                            exception));
                    return;
                }

                _acquisition.TrySetResult(
                    new MutexLease(this, outcome == WaitOutcome.Abandoned));
                _releaseSignal.WaitOne();
            }
            finally
            {
                if (ownsMutex)
                {
                    try
                    {
                        mutex!.ReleaseMutex();
                    }
                    catch (Exception exception) when (IsSynchronizationFailure(exception))
                    {
                        // Exiting the owner thread abandons the mutex if release failed.
                    }
                }

                mutex?.Dispose();
                _releaseSignal.Dispose();
                Interlocked.Decrement(ref _activeOwnerWorkerCount);
                _releaseCompletion.TrySetResult();
            }
        }

        private WaitOutcome WaitForOwnership(Mutex mutex)
        {
            using var cancellationSignal = new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset);
            using CancellationTokenRegistration registration =
                _cancellationToken.UnsafeRegister(
                    static state => ((EventWaitHandle)state!).Set(),
                    cancellationSignal);

            _cancellationToken.ThrowIfCancellationRequested();
            try
            {
                int waitMilliseconds = _timeout.TotalMilliseconds >= int.MaxValue
                    ? int.MaxValue
                    : (int)Math.Ceiling(_timeout.TotalMilliseconds);
                int signaled = WaitHandle.WaitAny(
                    [mutex, cancellationSignal],
                    waitMilliseconds);
                if (signaled == 0)
                {
                    if (_cancellationToken.IsCancellationRequested)
                    {
                        mutex.ReleaseMutex();
                        _cancellationToken.ThrowIfCancellationRequested();
                    }

                    return WaitOutcome.Acquired;
                }

                if (signaled == 1)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }

                if (signaled == WaitHandle.WaitTimeout)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        "Timed out waiting for configuration publication ownership.");
                }

                throw new InvalidOperationException(
                    "The configuration publication mutex wait returned unexpectedly.");
            }
            catch (AbandonedMutexException exception) when (exception.MutexIndex == 0)
            {
                if (_cancellationToken.IsCancellationRequested)
                {
                    mutex.ReleaseMutex();
                    _cancellationToken.ThrowIfCancellationRequested();
                }

                // The previous process may have stopped anywhere. The caller holds
                // the mutex now and must re-run every write condition before publish.
                return WaitOutcome.Abandoned;
            }
        }
    }

    private sealed class MutexLease : IConfigurationPublicationLease
    {
        private readonly MutexOwnerWorker _owner;
        private int _disposeRequested;

        internal MutexLease(MutexOwnerWorker owner, bool previousOwnerAbandoned)
        {
            _owner = owner;
            PreviousOwnerAbandoned = previousOwnerAbandoned;
        }

        public bool PreviousOwnerAbandoned { get; }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) == 0)
            {
                _owner.RequestRelease();
            }

            return new ValueTask(_owner.ReleaseCompletion);
        }
    }

    private sealed class ProcessSerializedLease : IConfigurationPublicationLease
    {
        private readonly IConfigurationPublicationLease _mutexLease;
        private readonly object _sync = new();
        private Task? _releaseTask;

        internal ProcessSerializedLease(IConfigurationPublicationLease mutexLease)
        {
            _mutexLease = mutexLease;
        }

        public bool PreviousOwnerAbandoned => _mutexLease.PreviousOwnerAbandoned;

        public ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                _releaseTask ??= ReleaseAsync();
                return new ValueTask(_releaseTask);
            }
        }

        private async Task ReleaseAsync()
        {
            try
            {
                await _mutexLease.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                ProcessPublicationGate.Release();
            }
        }
    }

    private sealed class NoOpLease : IConfigurationPublicationLease
    {
        internal static NoOpLease Instance { get; } = new();

        public bool PreviousOwnerAbandoned => false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
