using FileOrganizer.Core.Physical;

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
    /// <summary>The source identity bound by immediate revalidation.</summary>
    /// <remarks>It must equal the manifest identity before a move intent is published.</remarks>
    PhysicalEntryIdentity SourcePhysicalIdentity { get; }

    /// <summary>The destination directory identity bound by immediate revalidation.</summary>
    /// <remarks>It must equal the manifest identity before a move intent is published.</remarks>
    PhysicalEntryIdentity DestinationDirectoryPhysicalIdentity { get; }

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
