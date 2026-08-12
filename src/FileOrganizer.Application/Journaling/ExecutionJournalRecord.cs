using FileOrganizer.Application.Execution;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Journaling;

/// <summary>
/// An immutable logical journal record. A journal store is responsible for
/// publishing each record at its requested sequence atomically and durably.
/// </summary>
public sealed class ExecutionJournalRecord : IEquatable<ExecutionJournalRecord>
{
    private ExecutionJournalRecord(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc,
        ExecutionJournalRecordKind kind,
        int? operationOrdinal,
        PhysicalEntryIdentity? expectedEntryIdentity,
        JournalRecordReasonKind reason)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(fingerprint);

        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        bool isOperationRecord = kind is
            ExecutionJournalRecordKind.MoveIntent
            or ExecutionJournalRecordKind.MoveApplied
            or ExecutionJournalRecordKind.MoveNotApplied
            or ExecutionJournalRecordKind.MoveConflict
            or ExecutionJournalRecordKind.MoveAmbiguous
            or ExecutionJournalRecordKind.MoveManualRecovery
            or ExecutionJournalRecordKind.ExecutionStopped;

        if (isOperationRecord != operationOrdinal.HasValue)
        {
            throw new ArgumentException(
                "Only move records must identify a manifest operation.",
                nameof(operationOrdinal));
        }

        if (operationOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operationOrdinal));
        }

        if ((kind == ExecutionJournalRecordKind.MoveIntent)
            != (expectedEntryIdentity is not null))
        {
            throw new ArgumentException(
                "Only a move intent must durably retain its expected entry identity.",
                nameof(expectedEntryIdentity));
        }

        if (expectedEntryIdentity is not null
            && expectedEntryIdentity.EntryKind != PhysicalEntryKind.RegularFile)
        {
            throw new ArgumentException(
                "A move intent must retain a regular-file physical identity.",
                nameof(expectedEntryIdentity));
        }

        bool reasonIsRequired = kind is
            ExecutionJournalRecordKind.MoveNotApplied
            or ExecutionJournalRecordKind.MoveConflict
            or ExecutionJournalRecordKind.MoveAmbiguous
            or ExecutionJournalRecordKind.MoveManualRecovery
            or ExecutionJournalRecordKind.ExecutionStopped
            or ExecutionJournalRecordKind.ExecutionInterrupted;
        bool reasonIsAllowed = reasonIsRequired
            || kind == ExecutionJournalRecordKind.MoveApplied;

        if ((reasonIsRequired && reason == JournalRecordReasonKind.None)
            || (!reasonIsAllowed && reason != JournalRecordReasonKind.None))
        {
            throw new ArgumentException(
                reasonIsRequired
                    ? "This record kind requires a structured reason."
                    : "This record kind does not accept a failure reason.",
                nameof(reason));
        }

        RunId = runId;
        Fingerprint = fingerprint;
        Sequence = sequence;
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
        Kind = kind;
        OperationOrdinal = operationOrdinal;
        ExpectedEntryIdentity = expectedEntryIdentity;
        Reason = reason;
    }

    public ExecutionRunId RunId { get; }

    public PlanFingerprint Fingerprint { get; }

    public long Sequence { get; }

    public DateTimeOffset RecordedAtUtc { get; }

    public ExecutionJournalRecordKind Kind { get; }

    public int? OperationOrdinal { get; }

    /// <summary>
    /// Gets the strong identity observed immediately before a move intent was
    /// published. It is null for every other record kind.
    /// </summary>
    public PhysicalEntryIdentity? ExpectedEntryIdentity { get; }

    public JournalRecordReasonKind Reason { get; }

    public static ExecutionJournalRecord ExecutionStarted(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc) =>
        new(
            runId,
            fingerprint,
            sequence,
            recordedAtUtc,
            ExecutionJournalRecordKind.ExecutionStarted,
            null,
            null,
            JournalRecordReasonKind.None);

    public static ExecutionJournalRecord MoveIntent(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc,
        int operationOrdinal,
        PhysicalEntryIdentity expectedEntryIdentity) =>
        new(
            runId,
            fingerprint,
            sequence,
            recordedAtUtc,
            ExecutionJournalRecordKind.MoveIntent,
            operationOrdinal,
            expectedEntryIdentity,
            JournalRecordReasonKind.None);

    public static ExecutionJournalRecord MoveApplied(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc,
        int operationOrdinal,
        JournalRecordReasonKind reason = JournalRecordReasonKind.None)
    {
        RequireReason(
            reason,
            nameof(reason),
            JournalRecordReasonKind.None,
            JournalRecordReasonKind.RecoveryConfirmedApplied);
        return new(
            runId,
            fingerprint,
            sequence,
            recordedAtUtc,
            ExecutionJournalRecordKind.MoveApplied,
            operationOrdinal,
            null,
            reason);
    }

    public static ExecutionJournalRecord MoveNotApplied(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc,
        int operationOrdinal,
        JournalRecordReasonKind reason)
    {
        RequireReason(
            reason,
            nameof(reason),
            JournalRecordReasonKind.MoveDidNotApply,
            JournalRecordReasonKind.RecoveryConfirmedNotApplied,
            JournalRecordReasonKind.SourceRevalidationFailed,
            JournalRecordReasonKind.DestinationRevalidationFailed);
        return new(
            runId,
            fingerprint,
            sequence,
            recordedAtUtc,
            ExecutionJournalRecordKind.MoveNotApplied,
            operationOrdinal,
            null,
            reason);
    }

    public static ExecutionJournalRecord MoveConflict(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc,
        int operationOrdinal,
        JournalRecordReasonKind reason)
    {
        RequireReason(
            reason,
            nameof(reason),
            JournalRecordReasonKind.DestinationConflict,
            JournalRecordReasonKind.RecoverySourceIdentityChanged,
            JournalRecordReasonKind.RecoveryTargetIdentityChanged);
        return new(
            runId,
            fingerprint,
            sequence,
            recordedAtUtc,
            ExecutionJournalRecordKind.MoveConflict,
            operationOrdinal,
            null,
            reason);
    }

    public static ExecutionJournalRecord MoveAmbiguous(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc,
        int operationOrdinal,
        JournalRecordReasonKind reason)
    {
        RequireReason(
            reason,
            nameof(reason),
            JournalRecordReasonKind.MoveOutcomeUnknown,
            JournalRecordReasonKind.RecoverySourceIdentityChanged,
            JournalRecordReasonKind.RecoveryTargetIdentityChanged,
            JournalRecordReasonKind.RecoveryBothPathsContainExpectedEntry,
            JournalRecordReasonKind.RecoveryBothPathsMissing,
            JournalRecordReasonKind.RecoveryInspectionFailed);
        return new(
            runId,
            fingerprint,
            sequence,
            recordedAtUtc,
            ExecutionJournalRecordKind.MoveAmbiguous,
            operationOrdinal,
            null,
            reason);
    }

    public static ExecutionJournalRecord MoveManualRecovery(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc,
        int operationOrdinal,
        JournalRecordReasonKind reason)
    {
        RequireReason(
            reason,
            nameof(reason),
            JournalRecordReasonKind.RecoveryInspectionFailed);
        return new(
            runId,
            fingerprint,
            sequence,
            recordedAtUtc,
            ExecutionJournalRecordKind.MoveManualRecovery,
            operationOrdinal,
            null,
            reason);
    }

    /// <summary>
    /// Records a definitive pre-intent block or conflict. No mutation was
    /// attempted for this operation.
    /// </summary>
    public static ExecutionJournalRecord ExecutionStopped(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc,
        int operationOrdinal,
        JournalRecordReasonKind reason)
    {
        if (reason is not (
            JournalRecordReasonKind.SourceRevalidationFailed
            or JournalRecordReasonKind.DestinationRevalidationFailed
            or JournalRecordReasonKind.DestinationConflict))
        {
            throw new ArgumentException(
                "A pre-intent stop requires a revalidation or destination-conflict reason.",
                nameof(reason));
        }

        return new(
            runId,
            fingerprint,
            sequence,
            recordedAtUtc,
            ExecutionJournalRecordKind.ExecutionStopped,
            operationOrdinal,
            null,
            reason);
    }

    public static ExecutionJournalRecord ExecutionInterrupted(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc,
        JournalRecordReasonKind reason)
    {
        RequireReason(
            reason,
            nameof(reason),
            JournalRecordReasonKind.CancellationRequested,
            JournalRecordReasonKind.ExecutionFault);
        return new(
            runId,
            fingerprint,
            sequence,
            recordedAtUtc,
            ExecutionJournalRecordKind.ExecutionInterrupted,
            null,
            null,
            reason);
    }

    /// <summary>
    /// Records explicit authorization to continue after an outcome proven not
    /// to have applied or after cancellation at a safe boundary.
    /// </summary>
    public static ExecutionJournalRecord ExecutionResumed(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc) =>
        new(
            runId,
            fingerprint,
            sequence,
            recordedAtUtc,
            ExecutionJournalRecordKind.ExecutionResumed,
            null,
            null,
            JournalRecordReasonKind.None);

    public static ExecutionJournalRecord ExecutionCompleted(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc) =>
        new(
            runId,
            fingerprint,
            sequence,
            recordedAtUtc,
            ExecutionJournalRecordKind.ExecutionCompleted,
            null,
            null,
            JournalRecordReasonKind.None);

    internal static ExecutionJournalRecord Restore(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc,
        ExecutionJournalRecordKind kind,
        int? operationOrdinal,
        PhysicalEntryIdentity? expectedEntryIdentity,
        JournalRecordReasonKind reason) =>
        new(
            runId,
            fingerprint,
            sequence,
            recordedAtUtc,
            kind,
            operationOrdinal,
            expectedEntryIdentity,
            reason);

    public bool Equals(ExecutionJournalRecord? other) =>
        other is not null
        && RunId.Equals(other.RunId)
        && Fingerprint.Equals(other.Fingerprint)
        && Sequence == other.Sequence
        && RecordedAtUtc == other.RecordedAtUtc
        && Kind == other.Kind
        && OperationOrdinal == other.OperationOrdinal
        && Equals(ExpectedEntryIdentity, other.ExpectedEntryIdentity)
        && Reason == other.Reason;

    public override bool Equals(object? obj) => Equals(obj as ExecutionJournalRecord);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(RunId);
        hash.Add(Fingerprint);
        hash.Add(Sequence);
        hash.Add(RecordedAtUtc);
        hash.Add(Kind);
        hash.Add(OperationOrdinal);
        hash.Add(ExpectedEntryIdentity);
        hash.Add(Reason);
        return hash.ToHashCode();
    }

    private static void RequireReason(
        JournalRecordReasonKind reason,
        string parameterName,
        params JournalRecordReasonKind[] allowed)
    {
        if (!allowed.Contains(reason))
        {
            throw new ArgumentException(
                "The reason is not valid for this journal record kind.",
                parameterName);
        }
    }
}
