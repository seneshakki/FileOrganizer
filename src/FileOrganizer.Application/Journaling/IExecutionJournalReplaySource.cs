using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Journaling;

/// <summary>
/// Read-only boundary from durable journal validation into Application replay semantics.
/// Invalid durable input must never be exposed as a snapshot.
/// </summary>
/// <remarks>
/// A result is a point-in-time observation, not an execution lease or authorization.
/// A concurrent publisher may extend the journal after this read completes.
/// </remarks>
public interface IExecutionJournalReplaySource
{
    ValueTask<ExecutionJournalReadResult> ReadForReplayAsync(
        PlanFingerprint fingerprint,
        CancellationToken cancellationToken = default);
}
