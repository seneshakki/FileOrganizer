using System.Diagnostics;
using FileOrganizer.Application.Execution;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Journaling;

/// <summary>
/// Strictly reduces an append-only journal into immutable execution state.
/// The reducer has no clocks, I/O, or mutation capabilities.
/// </summary>
public static class ExecutionJournalStateMachine
{
    public static ExecutionJournalState Replay(
        ExecutionJournalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Records.Count == 0)
        {
            throw Problem(
                JournalSequenceProblemKind.StartRequired,
                -1,
                null,
                "An existing durable journal must contain its atomically published start record.");
        }

        return Replay(snapshot.Manifest, snapshot.Records);
    }

    public static ExecutionJournalState Replay(
        ExecutionManifest manifest,
        IEnumerable<ExecutionJournalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(records);

        int[] ordinals = manifest.Items.Select(item => item.Ordinal).ToArray();
        for (int index = 0; index < ordinals.Length; index++)
        {
            if (ordinals[index] != index)
            {
                throw Problem(
                    JournalSequenceProblemKind.InvalidManifestOrdinals,
                    -1,
                    null,
                    "Manifest operation ordinals must be unique, contiguous, and ordered from zero.");
            }
        }

        ExecutionJournalRecord[] recordSnapshot = [.. records];
        MutableOperationState[] operations = ordinals
            .Select(ordinal => new MutableOperationState(ordinal))
            .ToArray();

        ExecutionJournalStatus status = ExecutionJournalStatus.NotStarted;
        int? activeOrdinal = null;
        bool started = false;
        bool terminal = false;

        for (int index = 0; index < recordSnapshot.Length; index++)
        {
            ExecutionJournalRecord record = recordSnapshot[index]
                ?? throw new ArgumentException(
                    "Journal records cannot contain null.",
                    nameof(records));

            if (record.Sequence != index)
            {
                throw Problem(
                    JournalSequenceProblemKind.SequenceGap,
                    index,
                    record.Sequence,
                    $"Expected journal sequence {index}, but found {record.Sequence}.");
            }

            if (!record.Fingerprint.Equals(manifest.Fingerprint))
            {
                throw Problem(
                    JournalSequenceProblemKind.FingerprintMismatch,
                    index,
                    record.Sequence,
                    "A journal record belongs to a different plan fingerprint.");
            }

            if (terminal)
            {
                throw Problem(
                    JournalSequenceProblemKind.RecordAfterTerminal,
                    index,
                    record.Sequence,
                    "No journal record may follow a terminal execution state.");
            }

            if (record.Kind == ExecutionJournalRecordKind.ExecutionStarted)
            {
                if (started)
                {
                    throw Problem(
                        JournalSequenceProblemKind.DuplicateStart,
                        index,
                        record.Sequence,
                        "Execution can be started only once.");
                }

                if (index != 0)
                {
                    throw Problem(
                        JournalSequenceProblemKind.StartRequired,
                        index,
                        record.Sequence,
                        "ExecutionStarted must be the first journal record.");
                }

                started = true;
                status = ExecutionJournalStatus.InProgress;
                continue;
            }

            if (!started)
            {
                throw Problem(
                    JournalSequenceProblemKind.StartRequired,
                    index,
                    record.Sequence,
                    "ExecutionStarted must precede every other journal record.");
            }

            if (status == ExecutionJournalStatus.Resumable
                && record.Kind != ExecutionJournalRecordKind.ExecutionResumed)
            {
                throw Problem(
                    JournalSequenceProblemKind.ResumeRequired,
                    index,
                    record.Sequence,
                    "An explicit ExecutionResumed record is required before continuation.");
            }

            switch (record.Kind)
            {
                case ExecutionJournalRecordKind.MoveIntent:
                    ApplyIntent(record, index, operations, ref activeOrdinal, ref status);
                    break;

                case ExecutionJournalRecordKind.MoveApplied:
                    bool wasRecovered = record.Reason
                        == JournalRecordReasonKind.RecoveryConfirmedApplied;
                    ApplyOutcome(
                        record,
                        index,
                        operations,
                        JournalOperationStatus.Applied,
                        wasRecovered
                            ? ExecutionJournalStatus.Resumable
                            : ExecutionJournalStatus.InProgress,
                        isTerminal: false,
                        ref activeOrdinal,
                        ref status,
                        ref terminal);
                    break;

                case ExecutionJournalRecordKind.MoveNotApplied:
                    ApplyOutcome(
                        record,
                        index,
                        operations,
                        JournalOperationStatus.NotApplied,
                        ExecutionJournalStatus.Resumable,
                        isTerminal: false,
                        ref activeOrdinal,
                        ref status,
                        ref terminal);
                    break;

                case ExecutionJournalRecordKind.MoveConflict:
                    ApplyOutcome(
                        record,
                        index,
                        operations,
                        JournalOperationStatus.Conflict,
                        ExecutionJournalStatus.Conflict,
                        isTerminal: true,
                        ref activeOrdinal,
                        ref status,
                        ref terminal);
                    break;

                case ExecutionJournalRecordKind.MoveAmbiguous:
                    ApplyOutcome(
                        record,
                        index,
                        operations,
                        JournalOperationStatus.Ambiguous,
                        ExecutionJournalStatus.ManualRecovery,
                        isTerminal: true,
                        ref activeOrdinal,
                        ref status,
                        ref terminal);
                    break;

                case ExecutionJournalRecordKind.MoveManualRecovery:
                    ApplyOutcome(
                        record,
                        index,
                        operations,
                        JournalOperationStatus.ManualRecovery,
                        ExecutionJournalStatus.ManualRecovery,
                        isTerminal: true,
                        ref activeOrdinal,
                        ref status,
                        ref terminal);
                    break;

                case ExecutionJournalRecordKind.ExecutionStopped:
                    ApplyPreIntentStop(
                        record,
                        index,
                        operations,
                        ref activeOrdinal,
                        ref status,
                        ref terminal);
                    break;

                case ExecutionJournalRecordKind.ExecutionInterrupted:
                    if (activeOrdinal.HasValue)
                    {
                        throw Problem(
                            JournalSequenceProblemKind.InterruptionDuringPendingIntent,
                            index,
                            record.Sequence,
                            "A pending move intent must be reconciled before interruption is published.");
                    }

                    bool cancellation = record.Reason
                        == JournalRecordReasonKind.CancellationRequested;
                    status = cancellation
                        ? ExecutionJournalStatus.Resumable
                        : ExecutionJournalStatus.Interrupted;
                    terminal = !cancellation;
                    break;

                case ExecutionJournalRecordKind.ExecutionResumed:
                    if (status != ExecutionJournalStatus.Resumable)
                    {
                        throw Problem(
                            JournalSequenceProblemKind.ResumeNotAllowed,
                            index,
                            record.Sequence,
                            "Execution can resume only from an explicitly resumable state.");
                    }

                    status = ExecutionJournalStatus.InProgress;
                    break;

                case ExecutionJournalRecordKind.ExecutionCompleted:
                    if (activeOrdinal.HasValue)
                    {
                        throw Problem(
                            JournalSequenceProblemKind.OperationIntentRequired,
                            index,
                            record.Sequence,
                            "A pending move intent must be resolved before completion.");
                    }

                    if (operations.Any(operation => operation.Status != JournalOperationStatus.Applied))
                    {
                        throw Problem(
                            JournalSequenceProblemKind.CompletionBeforeAllApplied,
                            index,
                            record.Sequence,
                            "Execution cannot complete before every manifest operation is applied.");
                    }

                    status = ExecutionJournalStatus.Completed;
                    terminal = true;
                    break;

                case ExecutionJournalRecordKind.ExecutionStarted:
                default:
                    throw new UnreachableException();
            }
        }

        ExecutionJournalOperationState[] operationStates = operations
            .Select(operation => operation.ToImmutable())
            .ToArray();

        return new ExecutionJournalState(
            manifest.Fingerprint,
            status,
            operationStates,
            recordSnapshot.LongLength,
            activeOrdinal);
    }

    private static void ApplyIntent(
        ExecutionJournalRecord record,
        int recordIndex,
        MutableOperationState[] operations,
        ref int? activeOrdinal,
        ref ExecutionJournalStatus status)
    {
        if (activeOrdinal.HasValue)
        {
            throw Problem(
                JournalSequenceProblemKind.OperationAlreadyActive,
                recordIndex,
                record.Sequence,
                "Only one move intent may be pending at a time.");
        }

        MutableOperationState? expected = operations.FirstOrDefault(
            operation => operation.Status != JournalOperationStatus.Applied);

        if (expected is null || record.OperationOrdinal != expected.Ordinal)
        {
            throw Problem(
                JournalSequenceProblemKind.UnexpectedOperationOrdinal,
                recordIndex,
                record.Sequence,
                "Move intents must follow deterministic manifest order.");
        }

        if (expected.Status is not (
            JournalOperationStatus.NotStarted
            or JournalOperationStatus.NotApplied))
        {
            throw Problem(
                JournalSequenceProblemKind.UnexpectedOperationOrdinal,
                recordIndex,
                record.Sequence,
                "A resolved move cannot receive another intent in the same execution.");
        }

        expected.Set(
            JournalOperationStatus.IntentPublished,
            record.Sequence,
            JournalRecordReasonKind.None,
            record.ExpectedEntryIdentity);
        activeOrdinal = expected.Ordinal;
        status = ExecutionJournalStatus.AwaitingRecovery;
    }

    private static void ApplyOutcome(
        ExecutionJournalRecord record,
        int recordIndex,
        MutableOperationState[] operations,
        JournalOperationStatus operationStatus,
        ExecutionJournalStatus executionStatus,
        bool isTerminal,
        ref int? activeOrdinal,
        ref ExecutionJournalStatus status,
        ref bool terminal)
    {
        if (!activeOrdinal.HasValue || record.OperationOrdinal != activeOrdinal)
        {
            throw Problem(
                JournalSequenceProblemKind.OperationIntentRequired,
                recordIndex,
                record.Sequence,
                "A move outcome must resolve the currently pending intent.");
        }

        MutableOperationState operation = operations[activeOrdinal.Value];
        operation.Set(operationStatus, record.Sequence, record.Reason);
        activeOrdinal = null;
        status = executionStatus;
        terminal = isTerminal;
    }

    private static void ApplyPreIntentStop(
        ExecutionJournalRecord record,
        int recordIndex,
        MutableOperationState[] operations,
        ref int? activeOrdinal,
        ref ExecutionJournalStatus status,
        ref bool terminal)
    {
        if (activeOrdinal.HasValue)
        {
            throw Problem(
                JournalSequenceProblemKind.OperationAlreadyActive,
                recordIndex,
                record.Sequence,
                "A pre-intent stop cannot follow publication of a move intent.");
        }

        MutableOperationState? expected = operations.FirstOrDefault(
            operation => operation.Status != JournalOperationStatus.Applied);

        if (expected is null || record.OperationOrdinal != expected.Ordinal)
        {
            throw Problem(
                JournalSequenceProblemKind.UnexpectedOperationOrdinal,
                recordIndex,
                record.Sequence,
                "A pre-intent stop must identify the next manifest operation.");
        }

        bool isConflict = record.Reason == JournalRecordReasonKind.DestinationConflict;
        expected.Set(
            isConflict ? JournalOperationStatus.Conflict : JournalOperationStatus.Blocked,
            record.Sequence,
            record.Reason);
        activeOrdinal = null;
        status = isConflict
            ? ExecutionJournalStatus.Conflict
            : ExecutionJournalStatus.Blocked;
        terminal = true;
    }

    private static InvalidJournalSequenceException Problem(
        JournalSequenceProblemKind kind,
        int recordIndex,
        long? recordSequence,
        string message) => new(kind, recordIndex, recordSequence, message);

    private sealed class MutableOperationState
    {
        public MutableOperationState(int ordinal)
        {
            Ordinal = ordinal;
        }

        public int Ordinal { get; }

        public JournalOperationStatus Status { get; private set; }

        public long? LastRecordSequence { get; private set; }

        public JournalRecordReasonKind Reason { get; private set; }

        public PhysicalEntryIdentity? ExpectedEntryIdentity { get; private set; }

        public void Set(
            JournalOperationStatus status,
            long recordSequence,
            JournalRecordReasonKind reason,
            PhysicalEntryIdentity? expectedEntryIdentity = null)
        {
            Status = status;
            LastRecordSequence = recordSequence;
            Reason = reason;
            if (expectedEntryIdentity is not null)
            {
                ExpectedEntryIdentity = expectedEntryIdentity;
            }
        }

        public ExecutionJournalOperationState ToImmutable() =>
            new(Ordinal, Status, LastRecordSequence, Reason, ExpectedEntryIdentity);
    }
}
