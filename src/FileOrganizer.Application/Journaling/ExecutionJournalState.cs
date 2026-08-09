using System.Collections.ObjectModel;
using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Journaling;

/// <summary>An immutable state derived solely from durable journal records.</summary>
public sealed class ExecutionJournalState
{
    private readonly ReadOnlyCollection<ExecutionJournalOperationState> _operations;

    internal ExecutionJournalState(
        PlanFingerprint fingerprint,
        ExecutionJournalStatus status,
        IEnumerable<ExecutionJournalOperationState> operations,
        long nextSequence,
        int? activeOperationOrdinal)
    {
        Fingerprint = fingerprint;
        Status = status;
        _operations = Array.AsReadOnly(operations.ToArray());
        NextSequence = nextSequence;
        ActiveOperationOrdinal = activeOperationOrdinal;
    }

    public PlanFingerprint Fingerprint { get; }

    public ExecutionJournalStatus Status { get; }

    public IReadOnlyList<ExecutionJournalOperationState> Operations => _operations;

    public long NextSequence { get; }

    public int? ActiveOperationOrdinal { get; }

    /// <summary>
    /// Gets whether this record stream is append-terminal under the state
    /// machine. This does not mean the physical outcome is safely resolved:
    /// ManualRecovery is append-terminal but deliberately retains global
    /// unresolved-execution ownership.
    /// </summary>
    public bool IsTerminal => Status is
        ExecutionJournalStatus.Interrupted
        or ExecutionJournalStatus.Blocked
        or ExecutionJournalStatus.Conflict
        or ExecutionJournalStatus.ManualRecovery
        or ExecutionJournalStatus.Completed;

    /// <summary>
    /// Gets whether a future explicit-resume use case may safely continue this
    /// same journal. M3B-1 does not initiate that continuation automatically.
    /// </summary>
    public bool CanResume => Status == ExecutionJournalStatus.Resumable;

    /// <summary>
    /// Gets whether a journal implementation may allow a different fingerprint
    /// to begin. Completed, definitively blocked/conflicting, and non-resumable
    /// interrupted histories are safely closed. In-progress, pending-intent,
    /// resumable, and manual-recovery histories retain global ownership.
    /// </summary>
    public bool ReleasesGlobalExecutionOwnership => Status is
        ExecutionJournalStatus.Completed
        or ExecutionJournalStatus.Blocked
        or ExecutionJournalStatus.Conflict
        or ExecutionJournalStatus.Interrupted;
}
