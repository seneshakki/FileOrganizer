using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Application.Orchestration;

/// <summary>Acquires an immediately revalidated, abstract move lease.</summary>
/// <remarks>
/// Immediately before returning Ready, an implementation must recheck the
/// source metadata and source-root identity, bind a strong source identity,
/// verify the destination directory, and prove that the no-overwrite target
/// remains absent. The manifest supplies the declared source-root boundary.
/// The lease parameter is the capability proving that this physical boundary
/// is globally serialized; the caller must retain it for the returned move
/// lease's entire lifetime.
/// </remarks>
public interface IMoveOperationPort
{
    ValueTask<MoveLeaseAcquisition> TryAcquireAsync(
        IGlobalExecutionLease executionLease,
        ExecutionManifest manifest,
        ExecutionManifestItem item,
        CancellationToken cancellationToken = default);
}
