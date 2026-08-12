namespace FileOrganizer.Application.Journaling;

/// <summary>
/// A global execution/recovery lease shared by every executor and recoverer
/// using the journal, including callers in different processes. Disposing it
/// releases transient ownership; durable unresolved ownership remains governed
/// by the journal's atomic Begin contract.
/// </summary>
/// <remarks>
/// Implementations must release ownership idempotently. Cancellation after
/// acquisition does not revoke this capability; its lifetime ends only when it
/// is disposed.
/// </remarks>
public interface IGlobalExecutionLease : IAsyncDisposable
{
}
