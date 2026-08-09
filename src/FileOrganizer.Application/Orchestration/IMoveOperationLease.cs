using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Orchestration;

/// <summary>
/// Holds the immediate-revalidation boundary for one move. A future physical
/// implementation may keep safe handles alive for the lifetime of this lease.
/// </summary>
/// <remarks>
/// The lease must preserve the bound source identity and target-safety boundary
/// through CommitAsync. A future M3B-2 adapter must commit one move with native
/// no-overwrite semantics; it must never degrade to overwrite or copy/delete.
/// M3B-1 provides no physical implementation.
/// </remarks>
public interface IMoveOperationLease : IAsyncDisposable
{
    /// <summary>The identity that must be durably published with MoveIntent.</summary>
    EntryIdentity ExpectedSourceIdentity { get; }

    /// <summary>
    /// Invokes the abstract no-overwrite move primitive. User cancellation is
    /// intentionally absent after a durable intent exists.
    /// </summary>
    ValueTask<MoveAttemptResult> CommitAsync();

    /// <summary>
    /// Observes both paths after the attempt. This must run even when the
    /// primitive reports an expected failure.
    /// </summary>
    ValueTask<MoveObservation> ObserveAsync();
}
