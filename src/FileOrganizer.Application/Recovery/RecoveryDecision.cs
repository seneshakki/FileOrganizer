using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Application.Recovery;

/// <summary>
/// Combines a pure recovery classification with its idempotent publication
/// decision. A null record means the same resolution is already durable.
/// </summary>
public sealed class RecoveryDecision
{
    internal RecoveryDecision(
        RecoveryResolution resolution,
        RecoveryPublicationStatus publicationStatus,
        ExecutionJournalRecord? record)
    {
        Resolution = resolution;
        PublicationStatus = publicationStatus;
        Record = record;
    }

    public RecoveryResolution Resolution { get; }

    public RecoveryPublicationStatus PublicationStatus { get; }

    public ExecutionJournalRecord? Record { get; }
}
