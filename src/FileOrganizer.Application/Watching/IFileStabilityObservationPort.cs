using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Watching;

/// <summary>Performs one read-only physical observation for stability classification.</summary>
/// <remarks>Implementations must honor cancellation promptly and remain read-only.</remarks>
public interface IFileStabilityObservationPort
{
    ValueTask<PhysicalEntryObservation> ObserveAsync(
        CanonicalPath path,
        CancellationToken cancellationToken = default);
}
