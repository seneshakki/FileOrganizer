namespace FileOrganizer.Application.Journaling;

/// <summary>
/// Serializes all execution and recovery work, including work for different
/// plan fingerprints whose path sets might overlap. Implementations must provide
/// operating-system-enforced exclusivity across the supported process scope.
/// </summary>
public interface IGlobalExecutionLeaseProvider
{
    /// <summary>
    /// Attempts immediate or bounded acquisition. The timeout must be finite
    /// and non-negative. Cancellation before ownership follows normal .NET
    /// cancellation semantics and must not be converted to a result status.
    /// </summary>
    ValueTask<GlobalExecutionLeaseAcquisition> AcquireAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
