using System.Diagnostics;
using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Application.Orchestration;

/// <summary>
/// Reads validated durable journal state and projects it through the Application
/// reducer. This service has no clock, identifier generator, observation port,
/// or journal publication capability.
/// </summary>
public sealed class ExecutionJournalRecoveryInspector
{
    private readonly IExecutionJournalReplaySource _source;

    public ExecutionJournalRecoveryInspector(IExecutionJournalReplaySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public async ValueTask<ExecutionRecoveryResult> InspectAsync(
        PlanFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        cancellationToken.ThrowIfCancellationRequested();

        ExecutionJournalReadResult read = await _source.ReadForReplayAsync(
                fingerprint,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (read.Status == ExecutionJournalReadStatus.NotFound)
        {
            return new ExecutionRecoveryResult(
                fingerprint,
                ExecutionRecoveryOutcome.NoJournal,
                journalProblems: read.Problems);
        }

        if (read.Status == ExecutionJournalReadStatus.Invalid)
        {
            return Invalid(
                fingerprint,
                manifest: null,
                read.Problems,
                "The durable journal requires manual intervention before execution can continue.");
        }

        ExecutionJournalSnapshot snapshot = read.Snapshot!;
        if (!snapshot.Fingerprint.Equals(fingerprint))
        {
            return Invalid(
                fingerprint,
                manifest: null,
                [new ExecutionJournalReadProblem(
                    ExecutionJournalReadProblemKind.FingerprintOwnershipMismatch)],
                "The validated journal snapshot belongs to a different plan.");
        }

        ExecutionJournalState state;
        try
        {
            state = ExecutionJournalStateMachine.Replay(snapshot);
        }
        catch (InvalidJournalSequenceException exception)
        {
            ExecutionJournalReadProblem problem = new(
                ToReadProblemKind(exception.ProblemKind),
                exception.RecordSequence);
            return Invalid(
                fingerprint,
                snapshot.Manifest,
                [problem],
                "The durable journal contains an invalid execution-state transition.");
        }

        return Project(snapshot.Manifest, state, read.Problems);
    }

    private static ExecutionRecoveryResult Project(
        ExecutionManifest manifest,
        ExecutionJournalState state,
        IReadOnlyList<ExecutionJournalReadProblem> journalProblems)
    {
        int? nextOrdinal = NextOperationOrdinal(state);

        switch (state.Status)
        {
            case ExecutionJournalStatus.InProgress:
            case ExecutionJournalStatus.Resumable:
                return new ExecutionRecoveryResult(
                    manifest.Fingerprint,
                    ExecutionRecoveryOutcome.Resumable,
                    manifest,
                    nextOperationOrdinal: nextOrdinal,
                    journalState: state,
                    journalProblems: journalProblems);

            case ExecutionJournalStatus.AwaitingRecovery:
                int pendingOrdinal = state.ActiveOperationOrdinal
                    ?? throw new UnreachableException(
                        "Awaiting-recovery journal state must identify its pending move.");
                return new ExecutionRecoveryResult(
                    manifest.Fingerprint,
                    ExecutionRecoveryOutcome.RecoveryRequired,
                    manifest,
                    nextOperationOrdinal: pendingOrdinal,
                    problems:
                    [
                        new ExecutionProblem(
                            ExecutionProblemKind.RecoveryRequired,
                            "A durable move intent has no durable outcome and requires physical reconciliation.",
                            pendingOrdinal),
                    ],
                    journalState: state,
                    journalProblems: journalProblems);

            case ExecutionJournalStatus.Completed:
                return new ExecutionRecoveryResult(
                    manifest.Fingerprint,
                    ExecutionRecoveryOutcome.Completed,
                    manifest,
                    journalState: state,
                    journalProblems: journalProblems);

            case ExecutionJournalStatus.Blocked:
            case ExecutionJournalStatus.Conflict:
                ExecutionJournalOperationState stopped = state.Operations.Single(
                    operation => operation.Status is
                        JournalOperationStatus.Blocked or JournalOperationStatus.Conflict);
                ExecutionRecoveryOutcome stoppedOutcome = stopped.Status
                    == JournalOperationStatus.Conflict
                        ? ExecutionRecoveryOutcome.Conflict
                        : ExecutionRecoveryOutcome.Blocked;
                return new ExecutionRecoveryResult(
                    manifest.Fingerprint,
                    stoppedOutcome,
                    manifest,
                    nextOperationOrdinal: stopped.Ordinal,
                    problems: [ProblemFromStoppedOperation(stopped)],
                    journalState: state,
                    journalProblems: journalProblems);

            case ExecutionJournalStatus.ManualRecovery:
                ExecutionJournalOperationState unresolved = state.Operations.Single(
                    operation => operation.Status is
                        JournalOperationStatus.Ambiguous
                        or JournalOperationStatus.ManualRecovery);
                bool isAmbiguous = unresolved.Status == JournalOperationStatus.Ambiguous;
                return new ExecutionRecoveryResult(
                    manifest.Fingerprint,
                    isAmbiguous
                        ? ExecutionRecoveryOutcome.Ambiguous
                        : ExecutionRecoveryOutcome.ManualRecoveryRequired,
                    manifest,
                    nextOperationOrdinal: unresolved.Ordinal,
                    problems:
                    [
                        new ExecutionProblem(
                            isAmbiguous
                                ? ExecutionProblemKind.MoveOutcomeAmbiguous
                                : ExecutionProblemKind.RecoveryRequired,
                            isAmbiguous
                                ? "The durable journal records an ambiguous move outcome."
                                : "The durable journal records an operation requiring manual recovery.",
                            unresolved.Ordinal),
                    ],
                    journalState: state,
                    journalProblems: journalProblems);

            case ExecutionJournalStatus.Interrupted:
                return new ExecutionRecoveryResult(
                    manifest.Fingerprint,
                    ExecutionRecoveryOutcome.Interrupted,
                    manifest,
                    nextOperationOrdinal: nextOrdinal,
                    problems:
                    [
                        new ExecutionProblem(
                            ExecutionProblemKind.JournalFailure,
                            "The durable run ended after a non-resumable orchestration fault."),
                    ],
                    journalState: state,
                    journalProblems: journalProblems);

            case ExecutionJournalStatus.NotStarted:
            default:
                return Invalid(
                    manifest.Fingerprint,
                    manifest,
                    [new ExecutionJournalReadProblem(
                        ExecutionJournalReadProblemKind.SequenceViolation)],
                    "The journal reducer produced an invalid recovery state.");
        }
    }

    private static ExecutionRecoveryResult Invalid(
        PlanFingerprint fingerprint,
        ExecutionManifest? manifest,
        IEnumerable<ExecutionJournalReadProblem> journalProblems,
        string description) => new(
            fingerprint,
            ExecutionRecoveryOutcome.InvalidJournal,
            manifest,
            problems:
            [
                new ExecutionProblem(
                    ExecutionProblemKind.InvalidJournal,
                    description),
            ],
            journalProblems: journalProblems);

    private static int? NextOperationOrdinal(ExecutionJournalState state) =>
        state.Operations.FirstOrDefault(
            operation => operation.Status != JournalOperationStatus.Applied)?.Ordinal;

    private static ExecutionProblem ProblemFromStoppedOperation(
        ExecutionJournalOperationState operation)
    {
        ExecutionProblemKind kind = operation.Status == JournalOperationStatus.Conflict
            ? ExecutionProblemKind.DestinationConflict
            : operation.Reason == JournalRecordReasonKind.SourceRevalidationFailed
                ? ExecutionProblemKind.SourceRevalidationFailed
                : ExecutionProblemKind.DestinationRevalidationFailed;
        return new ExecutionProblem(
            kind,
            "The durable journal records a definitive execution stop.",
            operation.Ordinal);
    }

    private static ExecutionJournalReadProblemKind ToReadProblemKind(
        JournalSequenceProblemKind kind) => kind switch
        {
            JournalSequenceProblemKind.FingerprintMismatch =>
                ExecutionJournalReadProblemKind.FingerprintOwnershipMismatch,
            JournalSequenceProblemKind.RunMismatch =>
                ExecutionJournalReadProblemKind.RunOwnershipMismatch,
            JournalSequenceProblemKind.RecordAfterTerminal =>
                ExecutionJournalReadProblemKind.TerminalTail,
            _ => ExecutionJournalReadProblemKind.SequenceViolation,
        };
}
