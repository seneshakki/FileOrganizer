namespace FileOrganizer.Infrastructure.Configuration;

/// <summary>
/// Serializes same-process configuration access without retaining a gate for
/// every caller-selected path for the lifetime of the process.
/// </summary>
internal sealed class ConfigurationPathGateRegistry
{
    private readonly Dictionary<string, GateEntry> _entries;
    private readonly object _sync = new();

    internal ConfigurationPathGateRegistry()
    {
        _entries = new Dictionary<string, GateEntry>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
    }

    internal int ActiveGateCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    internal async ValueTask<IAsyncDisposable> AcquireAsync(
        string canonicalPathKey,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPathKey);
        ValidateTimeout(timeout);

        GateEntry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(canonicalPathKey, out entry!))
            {
                entry = new GateEntry();
                _entries.Add(canonicalPathKey, entry);
            }

            entry.ReferenceCount = checked(entry.ReferenceCount + 1);
        }

        try
        {
            bool acquired = await entry.Semaphore.WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            if (!acquired)
            {
                throw new TimeoutException(
                    "Timed out waiting for same-process configuration access.");
            }

            return new GateLease(this, canonicalPathKey, entry);
        }
        catch
        {
            ReleaseReference(canonicalPathKey, entry);
            throw;
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The path gate timeout must be finite and non-negative.");
        }
    }

    private void Release(
        string canonicalPathKey,
        GateEntry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(canonicalPathKey, entry);
    }

    private void ReleaseReference(
        string canonicalPathKey,
        GateEntry entry)
    {
        bool dispose;
        lock (_sync)
        {
            if (entry.ReferenceCount <= 0)
            {
                throw new InvalidOperationException(
                    "A configuration path gate reference was released more than once.");
            }

            entry.ReferenceCount--;
            dispose = entry.ReferenceCount == 0;
            if (dispose)
            {
                bool removed = _entries.Remove(canonicalPathKey, out GateEntry? removedEntry);
                if (!removed || !ReferenceEquals(entry, removedEntry))
                {
                    throw new InvalidOperationException(
                        "The configuration path gate registry lost ownership of a gate.");
                }
            }
        }

        if (dispose)
        {
            entry.Semaphore.Dispose();
        }
    }

    private sealed class GateEntry
    {
        internal int ReferenceCount { get; set; }

        internal SemaphoreSlim Semaphore { get; } = new(1, 1);
    }

    private sealed class GateLease : IAsyncDisposable
    {
        private readonly string _canonicalPathKey;
        private readonly GateEntry _entry;
        private ConfigurationPathGateRegistry? _owner;

        internal GateLease(
            ConfigurationPathGateRegistry owner,
            string canonicalPathKey,
            GateEntry entry)
        {
            _owner = owner;
            _canonicalPathKey = canonicalPathKey;
            _entry = entry;
        }

        public ValueTask DisposeAsync()
        {
            ConfigurationPathGateRegistry? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_canonicalPathKey, _entry);
            return ValueTask.CompletedTask;
        }
    }
}
