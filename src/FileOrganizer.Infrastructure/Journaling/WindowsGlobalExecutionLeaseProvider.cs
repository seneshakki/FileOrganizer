using System.Diagnostics;
using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Infrastructure.Journaling;

/// <summary>
/// Provides one OS-enforced execution/recovery owner in the current Windows
/// session. The fixed, versioned name contains no user-controlled data.
/// </summary>
/// <remarks>
/// The <c>Local\</c> namespace deliberately limits exclusivity to one Windows
/// session; it does not serialize separate interactive or service sessions.
/// Mutex abandonment reports a crashed owner, not safe journal or file state.
/// </remarks>
public sealed class WindowsGlobalExecutionLeaseProvider : IGlobalExecutionLeaseProvider
{
    internal const string MutexName = @"Local\FileOrganizer.ExecutionRecovery.v1";

    public ValueTask<GlobalExecutionLeaseAcquisition> AcquireAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(GlobalExecutionLeaseAcquisition.Unsupported());
        }

        var worker = new MutexOwnerWorker(timeout, cancellationToken);
        try
        {
            worker.Start();
        }
        catch (ThreadStateException)
        {
            worker.DisposeBeforeStart();
            return ValueTask.FromResult(GlobalExecutionLeaseAcquisition.Failed(
                GlobalExecutionLeaseFailureKind.SynchronizationFailure));
        }

        return new ValueTask<GlobalExecutionLeaseAcquisition>(worker.AcquisitionTask);
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The execution lease timeout must be finite and non-negative.");
        }
    }

    private enum WaitOutcome
    {
        Acquired,
        Abandoned,
        Busy,
    }

    /// <summary>
    /// A Windows mutex is owned by a thread, not merely by a process. This
    /// worker therefore performs acquisition and release on the same dedicated
    /// thread even when the caller crosses asynchronous continuation threads.
    /// </summary>
    private sealed class MutexOwnerWorker : IDisposable
    {
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<GlobalExecutionLeaseAcquisition> _acquisition =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly EventWaitHandle _releaseSignal = new(
            initialState: false,
            EventResetMode.ManualReset);
        private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
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
                Name = "FileOrganizer execution lease owner",
            };
        }

        internal Task<GlobalExecutionLeaseAcquisition> AcquisitionTask => _acquisition.Task;

        internal Task ReleaseCompletion => _releaseCompletion.Task;

        internal void Start() => _thread.Start();

        internal void RequestRelease()
        {
            if (Interlocked.Exchange(ref _releaseRequested, 1) == 0)
            {
                _releaseSignal.Set();
            }
        }

        public void Dispose() => RequestRelease();

        internal void DisposeBeforeStart()
        {
            Interlocked.Exchange(ref _releaseRequested, 1);
            _releaseSignal.Dispose();
            _releaseCompletion.TrySetResult();
        }

        private void Run()
        {
            Mutex? mutex = null;
            bool ownsMutex = false;
            Exception? releaseFailure = null;

            try
            {
                try
                {
                    mutex = new Mutex(initiallyOwned: false, MutexName);
                }
                catch (UnauthorizedAccessException)
                {
                    CompleteFailure(GlobalExecutionLeaseFailureKind.AccessDenied);
                    return;
                }
                catch (Exception exception) when (IsSynchronizationFailure(exception))
                {
                    CompleteFailure(GlobalExecutionLeaseFailureKind.SynchronizationFailure);
                    return;
                }

                WaitOutcome outcome;
                try
                {
                    outcome = WaitForOwnership(mutex);
                }
                catch (OperationCanceledException)
                {
                    _acquisition.TrySetCanceled(_cancellationToken);
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    CompleteFailure(GlobalExecutionLeaseFailureKind.AccessDenied);
                    return;
                }
                catch (Exception exception) when (IsSynchronizationFailure(exception))
                {
                    CompleteFailure(GlobalExecutionLeaseFailureKind.SynchronizationFailure);
                    return;
                }

                if (outcome == WaitOutcome.Busy)
                {
                    _acquisition.TrySetResult(GlobalExecutionLeaseAcquisition.Busy());
                    return;
                }

                ownsMutex = true;
                var lease = new MutexLease(this);
                _acquisition.TrySetResult(GlobalExecutionLeaseAcquisition.Acquired(
                    lease,
                    previousOwnerAbandoned: outcome == WaitOutcome.Abandoned));

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
                        releaseFailure = exception;
                    }
                }

                mutex?.Dispose();
                _releaseSignal.Dispose();

                if (releaseFailure is null)
                {
                    _releaseCompletion.TrySetResult();
                }
                else
                {
                    _releaseCompletion.TrySetException(releaseFailure);
                }
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
            WaitHandle[] handles = [mutex, cancellationSignal];
            bool firstAttempt = true;

            while (true)
            {
                TimeSpan elapsed = Stopwatch.GetElapsedTime(_startedTimestamp);
                TimeSpan remaining = _timeout - elapsed;
                if (!firstAttempt && remaining <= TimeSpan.Zero)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    return WaitOutcome.Busy;
                }

                int waitMilliseconds = firstAttempt && _timeout == TimeSpan.Zero
                    ? 0
                    : ToWaitMilliseconds(remaining);
                firstAttempt = false;

                try
                {
                    int signaled = WaitHandle.WaitAny(handles, waitMilliseconds);
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
                        return WaitOutcome.Busy;
                    }

                    if (signaled == WaitHandle.WaitTimeout)
                    {
                        continue;
                    }

                    throw new InvalidOperationException("The mutex wait returned an unknown result.");
                }
                catch (AbandonedMutexException exception) when (exception.MutexIndex == 0)
                {
                    if (_cancellationToken.IsCancellationRequested)
                    {
                        mutex.ReleaseMutex();
                        _cancellationToken.ThrowIfCancellationRequested();
                    }

                    return WaitOutcome.Abandoned;
                }
            }
        }

        private static int ToWaitMilliseconds(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero)
            {
                return 0;
            }

            return remaining.TotalMilliseconds >= int.MaxValue
                ? int.MaxValue
                : Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds));
        }

        private void CompleteFailure(GlobalExecutionLeaseFailureKind failureKind) =>
            _acquisition.TrySetResult(GlobalExecutionLeaseAcquisition.Failed(failureKind));

        private static bool IsSynchronizationFailure(Exception exception) => exception is
            IOException
            or WaitHandleCannotBeOpenedException
            or InvalidOperationException
            or ObjectDisposedException
            or ApplicationException;
    }

    private sealed class MutexLease : IGlobalExecutionLease
    {
        private readonly MutexOwnerWorker _owner;
        private int _disposeRequested;

        internal MutexLease(MutexOwnerWorker owner)
        {
            _owner = owner;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) == 0)
            {
                _owner.RequestRelease();
            }

            return new ValueTask(_owner.ReleaseCompletion);
        }
    }
}
