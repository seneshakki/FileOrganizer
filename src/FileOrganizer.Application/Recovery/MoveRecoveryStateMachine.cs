using System.Diagnostics;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Recovery;

/// <summary>
/// Reconciles a pending move intent from explicit observations. It never reads
/// or modifies the file system and never retries a move.
/// </summary>
public static class MoveRecoveryStateMachine
{
    public static RecoveryResolution Classify(
        PhysicalEntryIdentity expectedIdentity,
        RecoveryEntryObservation source,
        RecoveryEntryObservation target)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (source.Kind == RecoveryEntryObservationKind.InspectionFailed
            || target.Kind == RecoveryEntryObservationKind.InspectionFailed)
        {
            return new(
                RecoveryDisposition.Ambiguous,
                RecoveryReasonKind.InspectionFailed);
        }

        bool sourceIsExpected = IsExpected(source, expectedIdentity);
        bool targetIsExpected = IsExpected(target, expectedIdentity);

        if (sourceIsExpected && targetIsExpected)
        {
            return new(
                RecoveryDisposition.Ambiguous,
                RecoveryReasonKind.ExpectedEntryAtBothPaths);
        }

        if (!sourceIsExpected && targetIsExpected)
        {
            return new(
                RecoveryDisposition.Applied,
                RecoveryReasonKind.ExpectedEntryAtTargetOnly);
        }

        if (sourceIsExpected && target.Kind == RecoveryEntryObservationKind.Missing)
        {
            return new(
                RecoveryDisposition.NotApplied,
                RecoveryReasonKind.ExpectedEntryAtSourceOnly);
        }

        if (source.Kind == RecoveryEntryObservationKind.Missing
            && target.Kind == RecoveryEntryObservationKind.Missing)
        {
            return new(
                RecoveryDisposition.Ambiguous,
                RecoveryReasonKind.BothPathsMissing);
        }

        if (sourceIsExpected)
        {
            return new(
                RecoveryDisposition.Conflict,
                RecoveryReasonKind.TargetOccupiedByDifferentEntry);
        }

        return new(
            RecoveryDisposition.Ambiguous,
            source.Kind == RecoveryEntryObservationKind.Present
                ? RecoveryReasonKind.SourceIdentityChanged
                : RecoveryReasonKind.TargetOccupiedByDifferentEntry);
    }

    public static RecoveryDecision Decide(
        ExecutionRunId runId,
        ExecutionJournalState journalState,
        RecoveryEntryObservation source,
        RecoveryEntryObservation target,
        DateTimeOffset recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(journalState);
        ExecutionJournalOperationState? operation = journalState.ActiveOperationOrdinal is int ordinal
            ? journalState.Operations.Single(candidate => candidate.Ordinal == ordinal)
            : journalState.Operations.LastOrDefault(
                candidate => candidate.Status != JournalOperationStatus.NotStarted);
        PhysicalEntryIdentity expectedIdentity = operation?.ExpectedEntryIdentity
            ?? throw new InvalidOperationException(
                "The journal has no durable move-intent identity to recover.");
        RecoveryResolution resolution = Classify(expectedIdentity, source, target);

        if (journalState.ActiveOperationOrdinal is int activeOrdinal)
        {
            ExecutionJournalRecord record = CreateRecord(
                runId,
                journalState,
                activeOrdinal,
                resolution,
                recordedAtUtc);
            return new(
                resolution,
                RecoveryPublicationStatus.PublicationRequired,
                record);
        }

        if (StatusMatches(operation.Status, resolution.Disposition)
            && operation.Reason == ToJournalReason(resolution.Reason))
        {
            return new(
                resolution,
                RecoveryPublicationStatus.AlreadyRecorded,
                null);
        }

        throw new InvalidOperationException(
            "The journal has no pending intent or matching durable recovery outcome.");
    }

    private static ExecutionJournalRecord CreateRecord(
        ExecutionRunId runId,
        ExecutionJournalState state,
        int ordinal,
        RecoveryResolution resolution,
        DateTimeOffset recordedAtUtc)
    {
        JournalRecordReasonKind reason = ToJournalReason(resolution.Reason);

        return resolution.Disposition switch
        {
            RecoveryDisposition.Applied => ExecutionJournalRecord.MoveApplied(
                runId,
                state.Fingerprint,
                state.NextSequence,
                recordedAtUtc,
                ordinal,
                reason),
            RecoveryDisposition.NotApplied => ExecutionJournalRecord.MoveNotApplied(
                runId,
                state.Fingerprint,
                state.NextSequence,
                recordedAtUtc,
                ordinal,
                reason),
            RecoveryDisposition.Conflict => ExecutionJournalRecord.MoveConflict(
                runId,
                state.Fingerprint,
                state.NextSequence,
                recordedAtUtc,
                ordinal,
                reason),
            RecoveryDisposition.Ambiguous => ExecutionJournalRecord.MoveAmbiguous(
                runId,
                state.Fingerprint,
                state.NextSequence,
                recordedAtUtc,
                ordinal,
                reason),
            RecoveryDisposition.ManualRecovery => ExecutionJournalRecord.MoveManualRecovery(
                runId,
                state.Fingerprint,
                state.NextSequence,
                recordedAtUtc,
                ordinal,
                reason),
            _ => throw new UnreachableException(),
        };
    }

    private static JournalRecordReasonKind ToJournalReason(RecoveryReasonKind reason) =>
        reason switch
        {
            RecoveryReasonKind.ExpectedEntryAtTargetOnly =>
                JournalRecordReasonKind.RecoveryConfirmedApplied,
            RecoveryReasonKind.ExpectedEntryAtSourceOnly =>
                JournalRecordReasonKind.RecoveryConfirmedNotApplied,
            RecoveryReasonKind.SourceIdentityChanged =>
                JournalRecordReasonKind.RecoverySourceIdentityChanged,
            RecoveryReasonKind.TargetOccupiedByDifferentEntry =>
                JournalRecordReasonKind.RecoveryTargetIdentityChanged,
            RecoveryReasonKind.ExpectedEntryAtBothPaths =>
                JournalRecordReasonKind.RecoveryBothPathsContainExpectedEntry,
            RecoveryReasonKind.BothPathsMissing =>
                JournalRecordReasonKind.RecoveryBothPathsMissing,
            RecoveryReasonKind.InspectionFailed =>
                JournalRecordReasonKind.RecoveryInspectionFailed,
            _ => throw new UnreachableException(),
        };

    private static bool IsExpected(
        RecoveryEntryObservation observation,
        PhysicalEntryIdentity expectedIdentity) =>
        observation.Kind == RecoveryEntryObservationKind.Present
        && observation.Identity!.Equals(expectedIdentity);

    private static bool StatusMatches(
        JournalOperationStatus status,
        RecoveryDisposition disposition) =>
        (status, disposition) switch
        {
            (JournalOperationStatus.Applied, RecoveryDisposition.Applied) => true,
            (JournalOperationStatus.NotApplied, RecoveryDisposition.NotApplied) => true,
            (JournalOperationStatus.Conflict, RecoveryDisposition.Conflict) => true,
            (JournalOperationStatus.Ambiguous, RecoveryDisposition.Ambiguous) => true,
            (JournalOperationStatus.ManualRecovery, RecoveryDisposition.ManualRecovery) => true,
            _ => false,
        };
}
