using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Journaling;

/// <summary>
/// Immutable output of a durable read boundary. Only <see cref="ExecutionJournalReadStatus.Loaded"/>
/// may expose a snapshot to normal replay.
/// </summary>
public sealed class ExecutionJournalReadResult
{
    private readonly ReadOnlyCollection<ExecutionJournalReadProblem> _problems;

    public ExecutionJournalReadResult(
        ExecutionJournalReadStatus status,
        ExecutionJournalSnapshot? snapshot = null,
        IEnumerable<ExecutionJournalReadProblem>? problems = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ExecutionJournalReadProblem[] problemSnapshot = [.. problems ?? []];
        if (problemSnapshot.Any(static problem => problem is null))
        {
            throw new ArgumentException("Journal read problems cannot contain null.", nameof(problems));
        }

        Array.Sort(
            problemSnapshot,
            static (left, right) =>
            {
                int sequence = Nullable.Compare(left.RecordSequence, right.RecordSequence);
                return sequence != 0 ? sequence : left.Kind.CompareTo(right.Kind);
            });

        bool hasBlockingProblem = problemSnapshot.Any(
            static problem => problem.RequiresManualIntervention);
        bool validShape = status switch
        {
            ExecutionJournalReadStatus.NotFound => snapshot is null && !hasBlockingProblem,
            ExecutionJournalReadStatus.Loaded => snapshot is not null && !hasBlockingProblem,
            ExecutionJournalReadStatus.Invalid => snapshot is null && hasBlockingProblem,
            _ => false,
        };
        if (!validShape)
        {
            throw new ArgumentException(
                "Journal read status, snapshot, and problems describe contradictory state.",
                nameof(status));
        }

        Status = status;
        Snapshot = snapshot;
        _problems = Array.AsReadOnly(problemSnapshot);
    }

    public ExecutionJournalReadStatus Status { get; }

    public ExecutionJournalSnapshot? Snapshot { get; }

    public IReadOnlyList<ExecutionJournalReadProblem> Problems => _problems;
}
