using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Orchestration;

/// <summary>Acquires an immediately revalidated, abstract move lease.</summary>
/// <remarks>
/// Immediately before returning Ready, an implementation must recheck the
/// source metadata, bind a strong source identity, verify the destination
/// directory, and prove that the no-overwrite target remains absent. M3B-1 has
/// only scripted test implementations; the physical adapter belongs to M3B-2.
/// </remarks>
public interface IMoveOperationPort
{
    ValueTask<MoveLeaseAcquisition> TryAcquireAsync(
        ExecutionManifestItem item,
        CancellationToken cancellationToken = default);
}
