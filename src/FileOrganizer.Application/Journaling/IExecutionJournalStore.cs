using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Journaling;

/// <summary>
/// Abstracts an append-only execution journal without choosing a persistence
/// format or storage location.
/// </summary>
/// <remarks>
/// A successful publish must be atomic and durable before it returns. The
/// sequence is a compare-and-append boundary: publishing the exact record at
/// an occupied sequence is idempotent; a different record is a conflict; and
/// sequence gaps must not be accepted. Callers must hold the global execution
/// lease for every begin, publish, and read used to authorize mutation.
/// Observational replay reads grant no permission to continue operations.
/// Begin is also a global compare-and-create boundary across fingerprints: a
/// different journal whose reduced state does not release global ownership
/// must atomically block creation and be returned to the caller. A decodable
/// but invalid history blocks conservatively; an unreadable store fails the
/// operation rather than permitting a new execution.
/// </remarks>
public interface IExecutionJournalStore : IExecutionJournalReplaySource
{
    /// <summary>Returns null only when no journal exists for the fingerprint.</summary>
    ValueTask<ExecutionJournalSnapshot?> ReadAsync(
        PlanFingerprint fingerprint,
        CancellationToken cancellationToken = default);

    async ValueTask<ExecutionJournalReadResult> IExecutionJournalReplaySource.ReadForReplayAsync(
        PlanFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        ExecutionJournalSnapshot? snapshot = await ReadAsync(fingerprint, cancellationToken)
            .ConfigureAwait(false);
        return snapshot is null
            ? new ExecutionJournalReadResult(ExecutionJournalReadStatus.NotFound)
            : new ExecutionJournalReadResult(ExecutionJournalReadStatus.Loaded, snapshot);
    }

    /// <summary>
    /// Atomically creates a journal by durably publishing both the complete
    /// manifest and its sequence-zero ExecutionStarted record. If a journal
    /// with this fingerprint already exists, returns it unchanged. If another
    /// fingerprint owns unresolved state, returns that blocking snapshot and
    /// does not create a journal. Safely closed histories may coexist.
    /// </summary>
    ValueTask<JournalBeginResult> BeginAsync(
        ExecutionManifest manifest,
        ExecutionJournalRecord startRecord,
        CancellationToken cancellationToken = default);

    ValueTask<JournalPublishResult> PublishAsync(
        ExecutionJournalRecord record,
        CancellationToken cancellationToken = default);
}
