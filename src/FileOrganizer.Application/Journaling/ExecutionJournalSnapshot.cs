using System.Collections.ObjectModel;
using FileOrganizer.Application.Execution;

namespace FileOrganizer.Application.Journaling;

/// <summary>An immutable read of all durable records for one plan fingerprint.</summary>
public sealed class ExecutionJournalSnapshot
{
    private readonly ReadOnlyCollection<ExecutionJournalRecord> _records;

    public ExecutionJournalSnapshot(
        ExecutionManifest manifest,
        IEnumerable<ExecutionJournalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(records);

        ExecutionJournalRecord[] snapshot = [.. records];
        foreach (ExecutionJournalRecord record in snapshot)
        {
            ArgumentNullException.ThrowIfNull(record, nameof(records));
        }

        Manifest = manifest;
        _records = Array.AsReadOnly(snapshot);
    }

    /// <summary>
    /// Gets the complete execution intent durably stored when this journal was
    /// created. The fingerprint is not the sole recovery input.
    /// </summary>
    public ExecutionManifest Manifest { get; }

    public PlanFingerprint Fingerprint => Manifest.Fingerprint;

    /// <summary>Gets records in their durable publication order.</summary>
    public IReadOnlyList<ExecutionJournalRecord> Records => _records;
}
