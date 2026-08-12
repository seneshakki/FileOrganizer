using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Application.Orchestration;

/// <summary>Reads source and target state for recovery without invoking a move.</summary>
/// <remarks>
/// The implementation must inspect exactly the durable manifest source and
/// target paths, must not infer state from another path, and must not modify the
/// file system. The lease parameter is the capability proving that physical
/// recovery is globally serialized; the caller must keep it alive until the
/// observation completes. M3B-1 provides only a scripted test implementation.
/// </remarks>
public interface IRecoveryObservationPort
{
    ValueTask<MoveObservation> ObserveAsync(
        IGlobalExecutionLease executionLease,
        ExecutionManifestItem item,
        CancellationToken cancellationToken = default);
}
