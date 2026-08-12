using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Application.Orchestration;

/// <summary>Performs the whole-manifest read-only preflight before a journal begins.</summary>
/// <remarks>
/// An implementation must inspect every manifest item without mutation, recheck
/// the complete source metadata precondition, verify the destination directory,
/// and prove the no-overwrite target is absent. The lease parameter is the
/// capability proving that this physical boundary is globally serialized; the
/// caller must keep it alive until inspection completes. It must return one
/// result for every item rather than stopping at the first failure. M3B-1
/// intentionally provides no physical file-system implementation of this port.
/// </remarks>
public interface IExecutionPreflightPort
{
    ValueTask<ExecutionPreflightResult> InspectAsync(
        IGlobalExecutionLease executionLease,
        ExecutionManifest manifest,
        CancellationToken cancellationToken = default);
}
