using System.Diagnostics;
using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Application.Recovery;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;

namespace FileOrganizer.Application.Orchestration;

/// <summary>
/// Coordinates deterministic, journal-first move execution through abstract
/// ports. M3B-1 supplies no physical implementation of the move capability.
/// </summary>
public sealed class OrganizationPlanExecutor
{
    private readonly IExecutionPreflightPort _preflight;
    private readonly IMoveOperationPort _moves;
    private readonly IRecoveryObservationPort _recoveryObservations;
    private readonly IExecutionJournalStore _journal;
    private readonly IGlobalExecutionLeaseProvider _globalLease;
    private readonly TimeProvider _timeProvider;

    public OrganizationPlanExecutor(
        IExecutionPreflightPort preflight,
        IMoveOperationPort moves,
        IRecoveryObservationPort recoveryObservations,
        IExecutionJournalStore journal,
        IGlobalExecutionLeaseProvider globalLease,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(recoveryObservations);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(globalLease);

        _preflight = preflight;
        _moves = moves;
        _recoveryObservations = recoveryObservations;
        _journal = journal;
        _globalLease = globalLease;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Compiles and executes an eligible move-only plan through abstract ports.</summary>
    public async ValueTask<ExecutionResult> ExecuteAsync(
        OrganizationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        DateTimeOffset attemptStartedAt = UtcNow();
        cancellationToken.ThrowIfCancellationRequested();

        ExecutionManifestCompilation compilation = ExecutionManifestCompiler.Compile(plan);
        if (!compilation.Succeeded)
        {
            ExecutionProblem problem = new(
                ExecutionProblemKind.ManifestRejected,
                "Execution manifest compilation rejected the dry-run plan.");
            return new ExecutionResult(
                manifest: null,
                attemptStartedAt,
                NotBefore(attemptStartedAt),
                ExecutionOutcome.Rejected,
                items: [],
                problems: [problem],
                compilation.Problems);
        }

        ExecutionManifest manifest = compilation.Manifest!;
        if (manifest.IsEmpty)
        {
            return new ExecutionResult(
                manifest,
                attemptStartedAt,
                NotBefore(attemptStartedAt),
                ExecutionOutcome.NoWork,
                items: [],
                problems: []);
        }

        cancellationToken.ThrowIfCancellationRequested();
        GlobalExecutionLeaseAcquisition leaseAcquisition =
            await _globalLease.AcquireAsync(TimeSpan.Zero, cancellationToken)
                .ConfigureAwait(false);
        if (leaseAcquisition.Status != GlobalExecutionLeaseAcquisitionStatus.Acquired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CreateNotAttemptedResult(
                manifest,
                attemptStartedAt,
                ExecutionOutcome.Stopped,
                [LeaseAcquisitionProblem(leaseAcquisition)]);
        }

        IGlobalExecutionLease executionLease = leaseAcquisition.Lease!;
        await using (executionLease.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionJournalReadResult existing = await _journal.ReadForReplayAsync(
                    manifest.Fingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (existing.Status == ExecutionJournalReadStatus.Invalid)
            {
                return InvalidExistingJournalResult(
                    manifest,
                    attemptStartedAt,
                    existing.Problems);
            }

            if (existing.Status == ExecutionJournalReadStatus.Loaded)
            {
                return ExistingExecutionResult(
                    manifest,
                    existing.Snapshot!,
                    attemptStartedAt);
            }

            ExecutionPreflightResult preflight = await _preflight.InspectAsync(
                    executionLease,
                    manifest,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePreflightShape(manifest, preflight);

            ExecutionPreflightItemResult[] preflightFailures = preflight.Items
                .Where(item => item.Readiness != ExecutionReadiness.Ready)
                .ToArray();
            if (preflightFailures.Length != 0)
            {
                return CreatePreflightFailureResult(
                    manifest,
                    attemptStartedAt,
                    preflightFailures);
            }

            cancellationToken.ThrowIfCancellationRequested();

            ExecutionRunId runId = ExecutionRunId.CreateNew();
            DateTimeOffset lastJournalTime = UtcNow();
            ExecutionJournalRecord startRecord = ExecutionJournalRecord.ExecutionStarted(
                runId,
                manifest.Fingerprint,
                sequence: 0,
                lastJournalTime);
            JournalBeginResult begin = await _journal.BeginAsync(
                    manifest,
                    startRecord,
                    cancellationToken)
                .ConfigureAwait(false);

            if (begin.Status == JournalBeginStatus.InvalidExistingJournal)
            {
                return InvalidExistingJournalResult(
                    manifest,
                    attemptStartedAt,
                    begin.Problems);
            }

            if (begin.Status == JournalBeginStatus.AlreadyExists)
            {
                return ExistingExecutionResult(
                    manifest,
                    begin.Snapshot!,
                    attemptStartedAt);
            }

            if (begin.Status == JournalBeginStatus.BlockedByUnresolvedExecution)
            {
                ExecutionProblem busy = new(
                    ExecutionProblemKind.ExecutionAlreadyInProgress,
                    "A different unresolved execution journal blocks new execution.");
                ExecutionProblem recovery = new(
                    ExecutionProblemKind.RecoveryRequired,
                    $"Journal {begin.Snapshot!.Fingerprint.Value} requires recovery first.");
                return CreateNotAttemptedResult(
                    manifest,
                    attemptStartedAt,
                    ExecutionOutcome.RecoveryRequired,
                    [busy, recovery]);
            }

            ValidateBegunSnapshot(manifest, startRecord, begin.Snapshot!);

            long nextSequence = 1;
            ExecutionItemResult[] results = manifest.Items
                .Select(item => NotAttempted(item.Ordinal))
                .ToArray();

            for (int ordinal = 0; ordinal < manifest.Items.Count; ordinal++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return await InterruptAfterStartAsync(
                            manifest,
                            runId,
                            attemptStartedAt,
                            results,
                            nextSequence,
                            lastJournalTime)
                        .ConfigureAwait(false);
                }

                ExecutionManifestItem item = manifest.Items[ordinal];
                MoveLeaseAcquisition acquisition;
                try
                {
                    acquisition = await _moves.TryAcquireAsync(
                            executionLease,
                            item,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return await InterruptAfterStartAsync(
                            manifest,
                            runId,
                            attemptStartedAt,
                            results,
                            nextSequence,
                            lastJournalTime)
                        .ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    if (acquisition.Lease is not null)
                    {
                        await acquisition.Lease.DisposeAsync().ConfigureAwait(false);
                    }

                    return await InterruptAfterStartAsync(
                            manifest,
                            runId,
                            attemptStartedAt,
                            results,
                            nextSequence,
                            lastJournalTime)
                        .ConfigureAwait(false);
                }

                if (acquisition.Readiness != ExecutionReadiness.Ready)
                {
                    ValidateOperationProblem(item, acquisition.Problem!);
                    JournalRecordReasonKind reason = ToPreIntentStopReason(acquisition);
                    ExecutionJournalRecord stop = ExecutionJournalRecord.ExecutionStopped(
                        runId,
                        manifest.Fingerprint,
                        nextSequence,
                        NextJournalTime(ref lastJournalTime),
                        ordinal,
                        reason);
                    await PublishRequiredAsync(stop).ConfigureAwait(false);

                    ExecutionItemOutcome itemOutcome = acquisition.Readiness
                        == ExecutionReadiness.Conflict
                            ? ExecutionItemOutcome.Conflict
                            : ExecutionItemOutcome.Blocked;
                    results[ordinal] = new ExecutionItemResult(
                        ordinal,
                        itemOutcome,
                        [acquisition.Problem!]);

                    return CreateDefiniteStopResult(
                        manifest,
                        attemptStartedAt,
                        results,
                        acquisition.Problem!);
                }

                IMoveOperationLease moveLease = acquisition.Lease!;
                await using (moveLease.ConfigureAwait(false))
                {
                    ExecutionProblem? identityProblem = ValidateLeasePhysicalIdentities(
                        item,
                        moveLease);
                    if (identityProblem is not null)
                    {
                        JournalRecordReasonKind stopReason = identityProblem.Kind
                            == ExecutionProblemKind.SourceRevalidationFailed
                                ? JournalRecordReasonKind.SourceRevalidationFailed
                                : JournalRecordReasonKind.DestinationRevalidationFailed;
                        ExecutionJournalRecord stop = ExecutionJournalRecord.ExecutionStopped(
                            runId,
                            manifest.Fingerprint,
                            nextSequence,
                            NextJournalTime(ref lastJournalTime),
                            ordinal,
                            stopReason);
                        await PublishRequiredAsync(stop).ConfigureAwait(false);

                        results[ordinal] = new ExecutionItemResult(
                            ordinal,
                            ExecutionItemOutcome.Blocked,
                            [identityProblem]);
                        return CreateDefiniteStopResult(
                            manifest,
                            attemptStartedAt,
                            results,
                            identityProblem);
                    }

                    PhysicalEntryIdentity expectedSourceIdentity =
                        item.SourcePrecondition.PhysicalIdentity!;
                    ExecutionJournalRecord intent = ExecutionJournalRecord.MoveIntent(
                        runId,
                        manifest.Fingerprint,
                        nextSequence++,
                        NextJournalTime(ref lastJournalTime),
                        ordinal,
                        expectedSourceIdentity);
                    await PublishRequiredAsync(intent).ConfigureAwait(false);

                    // This is the critical boundary: unexpected failures intentionally
                    // escape with a durable pending intent for RecoverAsync to reconcile.
                    MoveAttemptResult attempt = await moveLease.CommitAsync()
                        .ConfigureAwait(false);
                    ValidateMoveAttempt(item, attempt);
                    MoveObservation observation = await moveLease.ObserveAsync()
                        .ConfigureAwait(false);

                    RecoveryResolution resolution = MoveRecoveryStateMachine.Classify(
                        expectedSourceIdentity,
                        observation.Source,
                        observation.Target);
                    ExecutionJournalRecord outcomeRecord = CreateMoveOutcomeRecord(
                        runId,
                        manifest.Fingerprint,
                        nextSequence++,
                        NextJournalTime(ref lastJournalTime),
                        ordinal,
                        resolution);
                    await PublishRequiredAsync(outcomeRecord).ConfigureAwait(false);

                    if (resolution.Disposition == RecoveryDisposition.Applied)
                    {
                        results[ordinal] = new ExecutionItemResult(
                            ordinal,
                            ExecutionItemOutcome.Applied,
                            []);
                        continue;
                    }

                    ExecutionProblem operationProblem = CreateMoveOutcomeProblem(
                        ordinal,
                        attempt,
                        resolution);
                    ExecutionItemOutcome operationOutcome = resolution.Disposition switch
                    {
                        RecoveryDisposition.NotApplied => ExecutionItemOutcome.NotApplied,
                        RecoveryDisposition.Conflict => ExecutionItemOutcome.Conflict,
                        RecoveryDisposition.Ambiguous or RecoveryDisposition.ManualRecovery =>
                            ExecutionItemOutcome.Ambiguous,
                        _ => throw new UnreachableException(),
                    };
                    results[ordinal] = new ExecutionItemResult(
                        ordinal,
                        operationOutcome,
                        [operationProblem]);

                    return resolution.Disposition is
                        RecoveryDisposition.Ambiguous or RecoveryDisposition.ManualRecovery
                            ? new ExecutionResult(
                                manifest,
                                attemptStartedAt,
                                NotBefore(attemptStartedAt),
                                ExecutionOutcome.RecoveryRequired,
                                results,
                                [operationProblem])
                            : CreateDefiniteStopResult(
                                manifest,
                                attemptStartedAt,
                                results,
                                operationProblem);
                }
            }

            ExecutionJournalRecord completed = ExecutionJournalRecord.ExecutionCompleted(
                runId,
                manifest.Fingerprint,
                nextSequence,
                NextJournalTime(ref lastJournalTime));
            await PublishRequiredAsync(completed).ConfigureAwait(false);

            return new ExecutionResult(
                manifest,
                attemptStartedAt,
                NotBefore(attemptStartedAt),
                ExecutionOutcome.Completed,
                results,
                problems: []);
        }
    }

    /// <summary>
    /// Reconciles durable journal state through read-only observations. This
    /// method never acquires a move lease and never invokes CommitAsync.
    /// </summary>
    public async ValueTask<ExecutionRecoveryResult> RecoverAsync(
        PlanFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        cancellationToken.ThrowIfCancellationRequested();

        GlobalExecutionLeaseAcquisition leaseAcquisition =
            await _globalLease.AcquireAsync(TimeSpan.Zero, cancellationToken)
                .ConfigureAwait(false);
        if (leaseAcquisition.Status != GlobalExecutionLeaseAcquisitionStatus.Acquired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionProblem leaseProblem = LeaseAcquisitionProblem(leaseAcquisition);
            return new ExecutionRecoveryResult(
                fingerprint,
                leaseAcquisition.Status == GlobalExecutionLeaseAcquisitionStatus.Busy
                    ? ExecutionRecoveryOutcome.Busy
                    : ExecutionRecoveryOutcome.LeaseUnavailable,
                problems: [leaseProblem]);
        }

        IGlobalExecutionLease executionLease = leaseAcquisition.Lease!;
        await using (executionLease.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionJournalReadResult read = await _journal.ReadForReplayAsync(
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
                return new ExecutionRecoveryResult(
                    fingerprint,
                    ExecutionRecoveryOutcome.InvalidJournal,
                    problems:
                    [
                        new ExecutionProblem(
                            ExecutionProblemKind.InvalidJournal,
                            "The durable journal requires manual intervention before recovery."),
                    ],
                    journalProblems: read.Problems);
            }

            ExecutionJournalSnapshot snapshot = read.Snapshot!;

            if (!snapshot.Fingerprint.Equals(fingerprint))
            {
                return new ExecutionRecoveryResult(
                    fingerprint,
                    ExecutionRecoveryOutcome.InvalidJournal,
                    problems:
                    [
                        new ExecutionProblem(
                            ExecutionProblemKind.InvalidJournal,
                            "The journal store returned a snapshot for a different fingerprint."),
                    ]);
            }

            ExecutionJournalState state;
            try
            {
                state = ExecutionJournalStateMachine.Replay(snapshot);
            }
            catch (InvalidJournalSequenceException exception)
            {
                return new ExecutionRecoveryResult(
                    fingerprint,
                    ExecutionRecoveryOutcome.InvalidJournal,
                    snapshot.Manifest,
                    problems:
                    [
                        new ExecutionProblem(
                            ExecutionProblemKind.InvalidJournal,
                            $"The execution journal is invalid: {exception.ProblemKind}."),
                    ]);
            }

            if (state.ActiveOperationOrdinal is null)
            {
                return await RecoverWithoutPendingIntentAsync(
                        snapshot,
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            int ordinal = state.ActiveOperationOrdinal.Value;
            ExecutionManifestItem item = snapshot.Manifest.Items[ordinal];
            MoveObservation observation = await _recoveryObservations.ObserveAsync(
                    executionLease,
                    item,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            DateTimeOffset lastJournalTime = LastJournalTime(snapshot);
            RecoveryDecision decision = MoveRecoveryStateMachine.Decide(
                snapshot.RunId,
                state,
                observation.Source,
                observation.Target,
                NextJournalTime(ref lastJournalTime));
            bool updated = false;
            if (decision.Record is not null)
            {
                await PublishRequiredAsync(decision.Record).ConfigureAwait(false);
                updated = true;
            }

            return RecoveryResultFromResolution(
                snapshot.Manifest,
                decision.Resolution,
                ordinal,
                updated);
        }
    }

    private async ValueTask<ExecutionRecoveryResult> RecoverWithoutPendingIntentAsync(
        ExecutionJournalSnapshot snapshot,
        ExecutionJournalState state,
        CancellationToken cancellationToken)
    {
        ExecutionManifest manifest = snapshot.Manifest;
        int? nextOrdinal = NextOperationOrdinal(state);

        switch (state.Status)
        {
            case ExecutionJournalStatus.Completed:
                return new ExecutionRecoveryResult(
                    manifest.Fingerprint,
                    ExecutionRecoveryOutcome.Completed,
                    manifest);

            case ExecutionJournalStatus.InProgress when nextOrdinal is null:
                cancellationToken.ThrowIfCancellationRequested();
                DateTimeOffset lastJournalTime = LastJournalTime(snapshot);
                ExecutionJournalRecord completed = ExecutionJournalRecord.ExecutionCompleted(
                    snapshot.RunId,
                    manifest.Fingerprint,
                    state.NextSequence,
                    NextJournalTime(ref lastJournalTime));
                await PublishRequiredAsync(completed).ConfigureAwait(false);
                return new ExecutionRecoveryResult(
                    manifest.Fingerprint,
                    ExecutionRecoveryOutcome.Completed,
                    manifest,
                    journalWasUpdated: true);

            case ExecutionJournalStatus.InProgress:
            case ExecutionJournalStatus.Resumable:
                return new ExecutionRecoveryResult(
                    manifest.Fingerprint,
                    ExecutionRecoveryOutcome.Resumable,
                    manifest,
                    nextOperationOrdinal: nextOrdinal);

            case ExecutionJournalStatus.Blocked:
                return new ExecutionRecoveryResult(
                    manifest.Fingerprint,
                    ExecutionRecoveryOutcome.Blocked,
                    manifest,
                    nextOperationOrdinal: nextOrdinal,
                    problems: [ProblemFromTerminalState(state)]);

            case ExecutionJournalStatus.Conflict:
                return new ExecutionRecoveryResult(
                    manifest.Fingerprint,
                    ExecutionRecoveryOutcome.Conflict,
                    manifest,
                    nextOperationOrdinal: nextOrdinal,
                    problems: [ProblemFromTerminalState(state)]);

            case ExecutionJournalStatus.ManualRecovery:
                ExecutionJournalOperationState manual = state.Operations.Last(
                    operation => operation.Status is
                        JournalOperationStatus.Ambiguous
                        or JournalOperationStatus.ManualRecovery);
                ExecutionRecoveryOutcome manualOutcome = manual.Status
                    == JournalOperationStatus.Ambiguous
                        ? ExecutionRecoveryOutcome.Ambiguous
                        : ExecutionRecoveryOutcome.ManualRecoveryRequired;
                return new ExecutionRecoveryResult(
                    manifest.Fingerprint,
                    manualOutcome,
                    manifest,
                    nextOperationOrdinal: manual.Ordinal,
                    problems:
                    [
                        new ExecutionProblem(
                            manualOutcome == ExecutionRecoveryOutcome.Ambiguous
                                ? ExecutionProblemKind.MoveOutcomeAmbiguous
                                : ExecutionProblemKind.RecoveryRequired,
                            "The journal contains an operation that requires manual recovery.",
                            manual.Ordinal),
                    ]);

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
                            "The execution stopped because of a non-resumable orchestration fault."),
                    ]);

            case ExecutionJournalStatus.NotStarted:
            case ExecutionJournalStatus.AwaitingRecovery:
            default:
                return new ExecutionRecoveryResult(
                    manifest.Fingerprint,
                    ExecutionRecoveryOutcome.InvalidJournal,
                    manifest,
                    problems:
                    [
                        new ExecutionProblem(
                            ExecutionProblemKind.InvalidJournal,
                            "The journal reducer produced an inconsistent recovery state."),
                    ]);
        }
    }

    private ExecutionResult InvalidExistingJournalResult(
        ExecutionManifest manifest,
        DateTimeOffset attemptStartedAt,
        IReadOnlyList<ExecutionJournalReadProblem> journalProblems)
    {
        string diagnosticKinds = string.Join(
            ", ",
            journalProblems
                .Where(static problem => problem.RequiresManualIntervention)
                .Select(static problem => problem.Kind)
                .Distinct()
                .Order());
        ExecutionProblem invalid = new(
            ExecutionProblemKind.InvalidJournal,
            diagnosticKinds.Length == 0
                ? "The durable journal is invalid and requires manual intervention."
                : $"The durable journal is invalid ({diagnosticKinds}) and requires manual intervention.");
        ExecutionProblem recovery = new(
            ExecutionProblemKind.RecoveryRequired,
            "Execution cannot continue until the invalid durable journal is resolved explicitly.");
        return CreateNotAttemptedResult(
            manifest,
            attemptStartedAt,
            ExecutionOutcome.RecoveryRequired,
            [invalid, recovery]);
    }

    private ExecutionResult ExistingExecutionResult(
        ExecutionManifest requestedManifest,
        ExecutionJournalSnapshot snapshot,
        DateTimeOffset attemptStartedAt)
    {
        if (!ExecutionManifestIdentity.AreExecutionCriticalFieldsEquivalent(
                requestedManifest,
                snapshot.Manifest))
        {
            ExecutionProblem invalid = new(
                ExecutionProblemKind.InvalidJournal,
                "The existing journal manifest differs in execution-critical fields.");
            ExecutionProblem recovery = new(
                ExecutionProblemKind.RecoveryRequired,
                "The existing journal must be inspected before this plan can execute.");
            return CreateNotAttemptedResult(
                requestedManifest,
                attemptStartedAt,
                ExecutionOutcome.RecoveryRequired,
                [invalid, recovery]);
        }

        ExecutionJournalState state;
        try
        {
            state = ExecutionJournalStateMachine.Replay(snapshot);
        }
        catch (InvalidJournalSequenceException exception)
        {
            ExecutionProblem invalid = new(
                ExecutionProblemKind.InvalidJournal,
                $"The existing journal is invalid: {exception.ProblemKind}.");
            ExecutionProblem recovery = new(
                ExecutionProblemKind.RecoveryRequired,
                "The invalid journal requires explicit recovery.");
            return CreateNotAttemptedResult(
                requestedManifest,
                attemptStartedAt,
                ExecutionOutcome.RecoveryRequired,
                [invalid, recovery]);
        }

        ExecutionManifest durableManifest = snapshot.Manifest;
        ExecutionItemResult[] items = ResultsFromJournal(state);
        DateTimeOffset startedAt = snapshot.Records.Count == 0
            ? attemptStartedAt
            : snapshot.Records[0].RecordedAtUtc;
        DateTimeOffset finishedAt = snapshot.Records.Count == 0
            ? NotBefore(startedAt)
            : snapshot.Records[^1].RecordedAtUtc;

        return state.Status switch
        {
            ExecutionJournalStatus.Completed => new ExecutionResult(
                durableManifest,
                startedAt,
                finishedAt,
                ExecutionOutcome.Completed,
                items,
                problems: [],
                wasReplayed: true),
            ExecutionJournalStatus.Blocked or ExecutionJournalStatus.Conflict =>
                ExistingDefiniteStopResult(
                    durableManifest,
                    startedAt,
                    finishedAt,
                    items),
            ExecutionJournalStatus.AwaitingRecovery
                or ExecutionJournalStatus.ManualRecovery => ExistingRecoveryRequiredResult(
                    durableManifest,
                    startedAt,
                    finishedAt,
                    items),
            ExecutionJournalStatus.NotStarted => CreateNotAttemptedResult(
                requestedManifest,
                attemptStartedAt,
                ExecutionOutcome.RecoveryRequired,
                [
                    new ExecutionProblem(
                        ExecutionProblemKind.InvalidJournal,
                        "The existing journal has no durable start record."),
                    new ExecutionProblem(
                        ExecutionProblemKind.RecoveryRequired,
                        "The incomplete journal requires explicit recovery."),
                ]),
            ExecutionJournalStatus.InProgress when items.All(
                item => item.Outcome == ExecutionItemOutcome.Applied) =>
                ExistingAllAppliedRecoveryResult(
                    durableManifest,
                    startedAt,
                    finishedAt,
                    items),
            ExecutionJournalStatus.InProgress or ExecutionJournalStatus.Resumable =>
                ExistingPausedResult(
                    durableManifest,
                    startedAt,
                    finishedAt,
                    items),
            ExecutionJournalStatus.Interrupted => ExistingFaultStoppedResult(
                durableManifest,
                startedAt,
                finishedAt,
                items),
            _ => ExistingPausedResult(
                durableManifest,
                startedAt,
                finishedAt,
                items),
        };
    }

    private static ExecutionResult ExistingDefiniteStopResult(
        ExecutionManifest manifest,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        ExecutionItemResult[] items)
    {
        ExecutionProblem[] problems = items.SelectMany(item => item.Problems).ToArray();
        bool anythingApplied = items.Any(item => item.Outcome == ExecutionItemOutcome.Applied);
        return new ExecutionResult(
            manifest,
            startedAt,
            finishedAt,
            anythingApplied ? ExecutionOutcome.PartiallyCompleted : ExecutionOutcome.Stopped,
            items,
            problems);
    }

    private static ExecutionResult ExistingRecoveryRequiredResult(
        ExecutionManifest manifest,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        ExecutionItemResult[] items)
    {
        ExecutionProblem[] problems =
        [
            .. items.SelectMany(item => item.Problems),
            new ExecutionProblem(
                ExecutionProblemKind.RecoveryRequired,
                "The durable execution state requires explicit recovery."),
        ];
        return new ExecutionResult(
            manifest,
            startedAt,
            finishedAt,
            ExecutionOutcome.RecoveryRequired,
            items,
            problems);
    }

    private static ExecutionResult ExistingAllAppliedRecoveryResult(
        ExecutionManifest manifest,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        ExecutionItemResult[] items)
    {
        ExecutionProblem recovery = new(
            ExecutionProblemKind.RecoveryRequired,
            "Every move is durable, but the journal completion record is missing.");
        return new ExecutionResult(
            manifest,
            startedAt,
            finishedAt,
            ExecutionOutcome.RecoveryRequired,
            items,
            [recovery]);
    }

    private static ExecutionResult ExistingPausedResult(
        ExecutionManifest manifest,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        ExecutionItemResult[] items)
    {
        ExecutionProblem recovery = new(
            ExecutionProblemKind.RecoveryRequired,
            "The durable execution is paused and must be recovered before continuation.");
        ExecutionProblem[] problems =
        [
            .. items.SelectMany(item => item.Problems),
            recovery,
        ];
        return new ExecutionResult(
            manifest,
            startedAt,
            finishedAt,
            ExecutionOutcome.RecoveryRequired,
            items,
            problems);
    }

    private static ExecutionResult ExistingFaultStoppedResult(
        ExecutionManifest manifest,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        ExecutionItemResult[] items)
    {
        ExecutionProblem interrupted = new(
            ExecutionProblemKind.JournalFailure,
            "The durable execution stopped because of a non-resumable orchestration fault.");
        ExecutionProblem[] problems =
        [
            .. items.SelectMany(item => item.Problems),
            interrupted,
        ];
        return new ExecutionResult(
            manifest,
            startedAt,
            finishedAt,
            items.Any(item => item.Outcome == ExecutionItemOutcome.Applied)
                ? ExecutionOutcome.PartiallyCompleted
                : ExecutionOutcome.Stopped,
            items,
            problems);
    }

    private async ValueTask<ExecutionResult> InterruptAfterStartAsync(
        ExecutionManifest manifest,
        ExecutionRunId runId,
        DateTimeOffset attemptStartedAt,
        ExecutionItemResult[] results,
        long nextSequence,
        DateTimeOffset lastJournalTime)
    {
        ExecutionJournalRecord interrupted = ExecutionJournalRecord.ExecutionInterrupted(
            runId,
            manifest.Fingerprint,
            nextSequence,
            NextJournalTime(ref lastJournalTime),
            JournalRecordReasonKind.CancellationRequested);
        await PublishRequiredAsync(interrupted).ConfigureAwait(false);

        ExecutionProblem cancellation = new(
            ExecutionProblemKind.CancellationRequested,
            "Cancellation was honored at a safe execution boundary.");
        return new ExecutionResult(
            manifest,
            attemptStartedAt,
            NotBefore(attemptStartedAt),
            ExecutionOutcome.Interrupted,
            results,
            [cancellation]);
    }

    private async ValueTask PublishRequiredAsync(ExecutionJournalRecord record)
    {
        JournalPublishResult result = await _journal.PublishAsync(
                record,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (result.Status == JournalPublishStatus.SequenceConflict
            || (result.Status == JournalPublishStatus.AlreadyPublished
                && !record.Equals(result.ExistingRecord)))
        {
            throw new InvalidOperationException(
                $"Journal sequence {record.Sequence} contains a different durable record.");
        }
    }

    private static ExecutionRecoveryResult RecoveryResultFromResolution(
        ExecutionManifest manifest,
        RecoveryResolution resolution,
        int ordinal,
        bool journalWasUpdated)
    {
        ExecutionRecoveryOutcome outcome = resolution.Disposition switch
        {
            RecoveryDisposition.Applied or RecoveryDisposition.NotApplied =>
                ExecutionRecoveryOutcome.Resumable,
            RecoveryDisposition.Conflict => ExecutionRecoveryOutcome.Conflict,
            RecoveryDisposition.Ambiguous => ExecutionRecoveryOutcome.Ambiguous,
            RecoveryDisposition.ManualRecovery =>
                ExecutionRecoveryOutcome.ManualRecoveryRequired,
            _ => throw new UnreachableException(),
        };
        int? nextOrdinal = resolution.Disposition == RecoveryDisposition.Applied
            ? ordinal + 1 < manifest.Items.Count ? ordinal + 1 : null
            : ordinal;
        ExecutionProblem[] problems = outcome switch
        {
            ExecutionRecoveryOutcome.Conflict =>
            [
                new ExecutionProblem(
                    ExecutionProblemKind.DestinationConflict,
                    "Recovery observed a source or destination identity conflict.",
                    ordinal),
            ],
            ExecutionRecoveryOutcome.Ambiguous or
                ExecutionRecoveryOutcome.ManualRecoveryRequired =>
            [
                new ExecutionProblem(
                    outcome == ExecutionRecoveryOutcome.Ambiguous
                        ? ExecutionProblemKind.MoveOutcomeAmbiguous
                        : ExecutionProblemKind.RecoveryRequired,
                    "Recovery could not prove whether the move applied.",
                    ordinal),
            ],
            _ => [],
        };

        return new ExecutionRecoveryResult(
            manifest.Fingerprint,
            outcome,
            manifest,
            resolution,
            nextOrdinal,
            journalWasUpdated,
            problems);
    }

    private static ExecutionProblem ProblemFromTerminalState(ExecutionJournalState state)
    {
        ExecutionJournalOperationState operation = state.Operations.Single(
            item => item.Status is JournalOperationStatus.Blocked or JournalOperationStatus.Conflict);
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

    private static int? NextOperationOrdinal(ExecutionJournalState state)
    {
        ExecutionJournalOperationState? next = state.Operations.FirstOrDefault(
            operation => operation.Status != JournalOperationStatus.Applied);
        return next?.Ordinal;
    }

    private static ExecutionJournalRecord CreateMoveOutcomeRecord(
        ExecutionRunId runId,
        PlanFingerprint fingerprint,
        long sequence,
        DateTimeOffset recordedAtUtc,
        int ordinal,
        RecoveryResolution resolution) => resolution.Disposition switch
        {
            RecoveryDisposition.Applied => ExecutionJournalRecord.MoveApplied(
                runId,
                fingerprint,
                sequence,
                recordedAtUtc,
                ordinal),
            RecoveryDisposition.NotApplied => ExecutionJournalRecord.MoveNotApplied(
                runId,
                fingerprint,
                sequence,
                recordedAtUtc,
                ordinal,
                JournalRecordReasonKind.MoveDidNotApply),
            RecoveryDisposition.Conflict => ExecutionJournalRecord.MoveConflict(
                runId,
                fingerprint,
                sequence,
                recordedAtUtc,
                ordinal,
                resolution.Reason == RecoveryReasonKind.SourceIdentityChanged
                    ? JournalRecordReasonKind.RecoverySourceIdentityChanged
                    : JournalRecordReasonKind.DestinationConflict),
            RecoveryDisposition.Ambiguous => ExecutionJournalRecord.MoveAmbiguous(
                runId,
                fingerprint,
                sequence,
                recordedAtUtc,
                ordinal,
                JournalRecordReasonKind.MoveOutcomeUnknown),
            RecoveryDisposition.ManualRecovery => ExecutionJournalRecord.MoveManualRecovery(
                runId,
                fingerprint,
                sequence,
                recordedAtUtc,
                ordinal,
                JournalRecordReasonKind.RecoveryInspectionFailed),
            _ => throw new UnreachableException(),
        };

    private static ExecutionProblem CreateMoveOutcomeProblem(
        int ordinal,
        MoveAttemptResult attempt,
        RecoveryResolution resolution) => resolution.Disposition switch
        {
            RecoveryDisposition.NotApplied => attempt.Problem
                ?? new ExecutionProblem(
                    ExecutionProblemKind.MoveFailed,
                    "The move was observed not to have applied.",
                    ordinal),
            RecoveryDisposition.Conflict => new ExecutionProblem(
                ExecutionProblemKind.DestinationConflict,
                "The post-move observation found a source or destination conflict.",
                ordinal),
            RecoveryDisposition.Ambiguous => new ExecutionProblem(
                ExecutionProblemKind.MoveOutcomeAmbiguous,
                "The post-move observation could not determine whether the move applied.",
                ordinal),
            RecoveryDisposition.ManualRecovery => new ExecutionProblem(
                ExecutionProblemKind.RecoveryRequired,
                "Post-move inspection failed and requires manual recovery.",
                ordinal),
            _ => throw new UnreachableException(),
        };

    private static JournalRecordReasonKind ToPreIntentStopReason(
        MoveLeaseAcquisition acquisition) => acquisition.Readiness switch
        {
            ExecutionReadiness.Conflict => JournalRecordReasonKind.DestinationConflict,
            ExecutionReadiness.Blocked
                when acquisition.Problem!.Kind == ExecutionProblemKind.SourceRevalidationFailed =>
                    JournalRecordReasonKind.SourceRevalidationFailed,
            ExecutionReadiness.Blocked =>
                JournalRecordReasonKind.DestinationRevalidationFailed,
            _ => throw new UnreachableException(),
        };

    private static ExecutionItemResult[] ResultsFromJournal(ExecutionJournalState state)
    {
        return state.Operations.Select(
            operation => operation.Status switch
            {
                JournalOperationStatus.Applied => new ExecutionItemResult(
                    operation.Ordinal,
                    ExecutionItemOutcome.Applied,
                    []),
                JournalOperationStatus.NotApplied => ItemWithProblem(
                    operation.Ordinal,
                    ExecutionItemOutcome.NotApplied,
                    ExecutionProblemKind.MoveFailed,
                    "The journal records that the move did not apply."),
                JournalOperationStatus.Blocked => ItemWithProblem(
                    operation.Ordinal,
                    ExecutionItemOutcome.Blocked,
                    operation.Reason == JournalRecordReasonKind.SourceRevalidationFailed
                        ? ExecutionProblemKind.SourceRevalidationFailed
                        : ExecutionProblemKind.DestinationRevalidationFailed,
                    "The journal records a failed execution precondition."),
                JournalOperationStatus.Conflict => ItemWithProblem(
                    operation.Ordinal,
                    ExecutionItemOutcome.Conflict,
                    ExecutionProblemKind.DestinationConflict,
                    "The journal records a destination conflict."),
                JournalOperationStatus.IntentPublished
                    or JournalOperationStatus.Ambiguous
                    or JournalOperationStatus.ManualRecovery => ItemWithProblem(
                        operation.Ordinal,
                        ExecutionItemOutcome.Ambiguous,
                        ExecutionProblemKind.RecoveryRequired,
                        "The journal records an operation requiring recovery."),
                JournalOperationStatus.NotStarted => NotAttempted(operation.Ordinal),
                _ => throw new UnreachableException(),
            }).ToArray();
    }

    private static ExecutionItemResult ItemWithProblem(
        int ordinal,
        ExecutionItemOutcome outcome,
        ExecutionProblemKind kind,
        string description)
    {
        ExecutionProblem problem = new(kind, description, ordinal);
        return new ExecutionItemResult(ordinal, outcome, [problem]);
    }

    private static ExecutionItemResult NotAttempted(int ordinal) =>
        new(ordinal, ExecutionItemOutcome.NotAttempted, []);

    private static ExecutionProblem LeaseAcquisitionProblem(
        GlobalExecutionLeaseAcquisition acquisition) => acquisition.Status switch
        {
            GlobalExecutionLeaseAcquisitionStatus.Busy => new ExecutionProblem(
                ExecutionProblemKind.ExecutionAlreadyInProgress,
                "Another execution or recovery operation currently owns the global lease."),
            GlobalExecutionLeaseAcquisitionStatus.Unsupported => new ExecutionProblem(
                ExecutionProblemKind.ExecutionLeaseUnsupported,
                "The current platform cannot provide the required execution lease."),
            GlobalExecutionLeaseAcquisitionStatus.Failed => new ExecutionProblem(
                ExecutionProblemKind.ExecutionLeaseAcquisitionFailed,
                "The operating system could not acquire the execution lease safely."),
            _ => throw new ArgumentException(
                "An acquired lease is not an acquisition failure.",
                nameof(acquisition)),
        };

    private ExecutionResult CreatePreflightFailureResult(
        ExecutionManifest manifest,
        DateTimeOffset startedAt,
        IReadOnlyList<ExecutionPreflightItemResult> failures)
    {
        ExecutionItemResult[] results = manifest.Items
            .Select(item => NotAttempted(item.Ordinal))
            .ToArray();
        foreach (ExecutionPreflightItemResult failure in failures)
        {
            results[failure.Ordinal] = new ExecutionItemResult(
                failure.Ordinal,
                failure.Readiness == ExecutionReadiness.Conflict
                    ? ExecutionItemOutcome.Conflict
                    : ExecutionItemOutcome.Blocked,
                [failure.Problem!]);
        }

        return new ExecutionResult(
            manifest,
            startedAt,
            NotBefore(startedAt),
            ExecutionOutcome.Stopped,
            results,
            failures.Select(failure => failure.Problem!));
    }

    private ExecutionResult CreateDefiniteStopResult(
        ExecutionManifest manifest,
        DateTimeOffset startedAt,
        ExecutionItemResult[] results,
        ExecutionProblem problem)
    {
        bool anythingApplied = results.Any(
            item => item.Outcome == ExecutionItemOutcome.Applied);
        return new ExecutionResult(
            manifest,
            startedAt,
            NotBefore(startedAt),
            anythingApplied ? ExecutionOutcome.PartiallyCompleted : ExecutionOutcome.Stopped,
            results,
            [problem]);
    }

    private ExecutionResult CreateNotAttemptedResult(
        ExecutionManifest manifest,
        DateTimeOffset startedAt,
        ExecutionOutcome outcome,
        IEnumerable<ExecutionProblem> problems) => new(
            manifest,
            startedAt,
            NotBefore(startedAt),
            outcome,
            manifest.Items.Select(item => NotAttempted(item.Ordinal)),
            problems);

    private static void ValidatePreflightShape(
        ExecutionManifest manifest,
        ExecutionPreflightResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Items.Count != manifest.Items.Count)
        {
            throw new InvalidOperationException(
                "The preflight port must report every manifest operation exactly once.");
        }
    }

    private static void ValidateOperationProblem(
        ExecutionManifestItem item,
        ExecutionProblem problem)
    {
        if (problem.OperationOrdinal != item.Ordinal)
        {
            throw new InvalidOperationException(
                "The move port returned a problem for a different manifest operation.");
        }
    }

    private static void ValidateMoveAttempt(
        ExecutionManifestItem item,
        MoveAttemptResult attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Problem is not null)
        {
            ValidateOperationProblem(item, attempt.Problem);
        }
    }

    private static ExecutionProblem? ValidateLeasePhysicalIdentities(
        ExecutionManifestItem item,
        IMoveOperationLease lease)
    {
        if (!item.SourcePrecondition.PhysicalIdentity!.Equals(lease.SourcePhysicalIdentity))
        {
            return new ExecutionProblem(
                ExecutionProblemKind.SourceRevalidationFailed,
                "The move lease is bound to a different physical source entry.",
                item.Ordinal);
        }

        if (!item.DestinationDirectoryPhysicalIdentity.Equals(
                lease.DestinationDirectoryPhysicalIdentity))
        {
            return new ExecutionProblem(
                ExecutionProblemKind.DestinationRevalidationFailed,
                "The move lease is bound to a different physical destination directory.",
                item.Ordinal);
        }

        return null;
    }

    private static void ValidateBegunSnapshot(
        ExecutionManifest manifest,
        ExecutionJournalRecord startRecord,
        ExecutionJournalSnapshot snapshot)
    {
        if (!ExecutionManifestIdentity.AreExecutionCriticalFieldsEquivalent(
                manifest,
                snapshot.Manifest)
            || snapshot.Records.Count != 1
            || !snapshot.Records[0].Equals(startRecord))
        {
            throw new InvalidOperationException(
                "The journal store returned an invalid newly begun snapshot.");
        }
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private DateTimeOffset NotBefore(DateTimeOffset lowerBound)
    {
        DateTimeOffset now = UtcNow();
        return now < lowerBound ? lowerBound : now;
    }

    private DateTimeOffset NextJournalTime(ref DateTimeOffset previous)
    {
        previous = NotBefore(previous);
        return previous;
    }

    private static DateTimeOffset LastJournalTime(ExecutionJournalSnapshot snapshot) =>
        snapshot.Records.Count == 0
            ? DateTimeOffset.MinValue
            : snapshot.Records[^1].RecordedAtUtc;
}
