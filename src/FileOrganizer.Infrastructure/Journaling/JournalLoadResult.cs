using System.Collections.ObjectModel;
using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Infrastructure.Journaling;

/// <summary>An immutable detailed load result; no corrupt input is repaired or skipped.</summary>
public sealed class JournalLoadResult
{
    private readonly ReadOnlyCollection<JournalLoadProblem> _problems;

    internal JournalLoadResult(
        ExecutionJournalSnapshot? snapshot,
        IEnumerable<JournalLoadProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        JournalLoadProblem[] copy = [.. problems];
        if (copy.Any(static problem => problem is null))
        {
            throw new ArgumentException("Journal problems cannot contain null.", nameof(problems));
        }

        Snapshot = snapshot;
        _problems = Array.AsReadOnly(copy);
        Status = snapshot is not null
            ? JournalLoadStatus.Loaded
            : copy.Any(static problem => problem.Kind != JournalLoadProblemKind.TemporaryFilePresent)
                ? JournalLoadStatus.Invalid
                : JournalLoadStatus.NotFound;
    }

    public JournalLoadStatus Status { get; }

    public ExecutionJournalSnapshot? Snapshot { get; }

    public IReadOnlyList<JournalLoadProblem> Problems => _problems;

    public bool Succeeded => Status == JournalLoadStatus.Loaded
        && !_problems.Any(static problem => problem.Kind != JournalLoadProblemKind.TemporaryFilePresent);
}
