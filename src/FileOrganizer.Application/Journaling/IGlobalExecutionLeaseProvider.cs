namespace FileOrganizer.Application.Journaling;

/// <summary>
/// Serializes all execution and recovery work, including work for different
/// plan fingerprints whose path sets might overlap. A real implementation must
/// provide exclusivity across every process sharing the journal; M3B-1 provides
/// only scripted test doubles.
/// </summary>
public interface IGlobalExecutionLeaseProvider
{
    /// <summary>Returns null when another execution or recovery owns the lease.</summary>
    ValueTask<IGlobalExecutionLease?> TryAcquireAsync(
        CancellationToken cancellationToken = default);
}
