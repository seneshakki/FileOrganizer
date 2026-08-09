namespace FileOrganizer.Application.Journaling;

/// <summary>
/// A global execution/recovery lease shared by every executor and recoverer
/// using the journal, including callers in different processes. Disposing it
/// releases transient ownership; durable unresolved ownership remains governed
/// by the journal's atomic Begin contract.
/// </summary>
public interface IGlobalExecutionLease : IAsyncDisposable
{
}
