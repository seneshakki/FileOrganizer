using System.Collections.ObjectModel;
using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Journaling;

/// <summary>
/// Returns the exact durable journal created, found by fingerprint, or found
/// blocking creation because its execution is still unresolved.
/// </summary>
public sealed class JournalBeginResult
{
    private readonly ReadOnlyCollection<ExecutionJournalReadProblem> _problems;

    public JournalBeginResult(
        JournalBeginStatus status,
        ExecutionJournalSnapshot snapshot)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentNullException.ThrowIfNull(snapshot);
        if (status == JournalBeginStatus.InvalidExistingJournal)
        {
            throw new ArgumentException(
                "An invalid existing journal cannot expose a trusted snapshot.",
                nameof(status));
        }

        Status = status;
        Snapshot = snapshot;
        ExistingFingerprint = snapshot.Fingerprint;
        _problems = Array.AsReadOnly(Array.Empty<ExecutionJournalReadProblem>());
    }

    private JournalBeginResult(
        PlanFingerprint existingFingerprint,
        IEnumerable<ExecutionJournalReadProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(existingFingerprint);
        ArgumentNullException.ThrowIfNull(problems);

        ExecutionJournalReadProblem[] problemSnapshot = [.. problems];
        if (problemSnapshot.Any(static problem => problem is null)
            || !problemSnapshot.Any(static problem => problem.RequiresManualIntervention))
        {
            throw new ArgumentException(
                "An invalid existing journal requires at least one blocking read problem.",
                nameof(problems));
        }

        Array.Sort(
            problemSnapshot,
            static (left, right) =>
            {
                int sequence = Nullable.Compare(left.RecordSequence, right.RecordSequence);
                return sequence != 0 ? sequence : left.Kind.CompareTo(right.Kind);
            });

        Status = JournalBeginStatus.InvalidExistingJournal;
        ExistingFingerprint = existingFingerprint;
        _problems = Array.AsReadOnly(problemSnapshot);
    }

    public JournalBeginStatus Status { get; }

    public ExecutionJournalSnapshot? Snapshot { get; }

    /// <summary>
    /// Gets the fingerprint of the trusted snapshot or invalid journal that
    /// prevented creation.
    /// </summary>
    public PlanFingerprint ExistingFingerprint { get; }

    public IReadOnlyList<ExecutionJournalReadProblem> Problems => _problems;

    public static JournalBeginResult InvalidExistingJournal(
        PlanFingerprint existingFingerprint,
        IEnumerable<ExecutionJournalReadProblem> problems) =>
        new(existingFingerprint, problems);
}
