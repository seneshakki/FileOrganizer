using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Orchestration;

/// <summary>Reads source and target state for recovery without invoking a move.</summary>
/// <remarks>
/// The implementation must inspect exactly the durable manifest source and
/// target paths, must not infer state from another path, and must not modify the
/// file system. M3B-1 provides only a scripted test implementation.
/// </remarks>
public interface IRecoveryObservationPort
{
    ValueTask<MoveObservation> ObserveAsync(
        ExecutionManifestItem item,
        CancellationToken cancellationToken = default);
}
