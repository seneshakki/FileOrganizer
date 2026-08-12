using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Application.Orchestration;
using FileOrganizer.Application.Recovery;
using FileOrganizer.Application.Tests.Execution;
using FileOrganizer.Application.Tests.Journaling;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Application.Tests.Orchestration;

internal sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => instant;
}

internal sealed class FakeGlobalExecutionLeaseProvider : IGlobalExecutionLeaseProvider
{
    private object? _owner;
    private int _acquisitionCount;

    internal GlobalExecutionLeaseAcquisitionStatus NextStatus { get; set; } =
        GlobalExecutionLeaseAcquisitionStatus.Acquired;

    internal GlobalExecutionLeaseFailureKind FailureKind { get; set; } =
        GlobalExecutionLeaseFailureKind.SynchronizationFailure;

    internal bool PreviousOwnerAbandoned { get; set; }

    internal int AcquisitionCount => Volatile.Read(ref _acquisitionCount);

    internal bool IsOwned => Volatile.Read(ref _owner) is not null;

    public ValueTask<GlobalExecutionLeaseAcquisition> AcquireAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout < TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Interlocked.Increment(ref _acquisitionCount);

        if (NextStatus == GlobalExecutionLeaseAcquisitionStatus.Busy)
        {
            return ValueTask.FromResult(GlobalExecutionLeaseAcquisition.Busy());
        }

        if (NextStatus == GlobalExecutionLeaseAcquisitionStatus.Unsupported)
        {
            return ValueTask.FromResult(GlobalExecutionLeaseAcquisition.Unsupported());
        }

        if (NextStatus == GlobalExecutionLeaseAcquisitionStatus.Failed)
        {
            return ValueTask.FromResult(GlobalExecutionLeaseAcquisition.Failed(FailureKind));
        }

        object owner = new();
        if (Interlocked.CompareExchange(ref _owner, owner, null) is not null)
        {
            return ValueTask.FromResult(GlobalExecutionLeaseAcquisition.Busy());
        }

        return ValueTask.FromResult(
            GlobalExecutionLeaseAcquisition.Acquired(
                new Lease(this, owner),
                PreviousOwnerAbandoned));
    }

    private void Release(object owner) =>
        Interlocked.CompareExchange(ref _owner, null, owner);

    private sealed class Lease(
        FakeGlobalExecutionLeaseProvider provider,
        object owner) : IGlobalExecutionLease
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                provider.Release(owner);
            }

            return ValueTask.CompletedTask;
        }
    }
}

internal enum JournalFaultBoundary
{
    BeforeBegin,
    AfterBegin,
    BeforePublish,
    AfterPublish,
}

internal sealed record JournalBoundary(
    JournalFaultBoundary Boundary,
    ExecutionJournalRecord Record);

internal sealed class ScriptedJournalStore : IExecutionJournalStore
{
    private readonly Dictionary<PlanFingerprint, JournalData> _journals = [];

    internal Action<JournalBoundary>? OnBoundary { get; set; }

    internal Func<PlanFingerprint, ExecutionJournalSnapshot?>? ReadOverride { get; set; }

    internal Func<PlanFingerprint, ExecutionJournalReadResult>? ReplayReadOverride
    {
        get;
        set;
    }

    internal Func<ExecutionManifest, ExecutionJournalRecord, JournalBeginResult?>?
        BeginOverride { get; set; }

    internal Func<ExecutionJournalRecord, JournalPublishResult?>? PublishOverride
    {
        get;
        set;
    }

    internal int BeginCount { get; private set; }

    internal int ReadCount { get; private set; }

    internal int PublishCount { get; private set; }

    internal IReadOnlyList<ExecutionJournalRecord> Records(PlanFingerprint fingerprint) =>
        _journals.TryGetValue(fingerprint, out JournalData? data)
            ? data.Records.AsReadOnly()
            : [];

    internal void Seed(
        ExecutionManifest manifest,
        IEnumerable<ExecutionJournalRecord> records) =>
        _journals.Add(
            manifest.Fingerprint,
            new JournalData(TestExecutionRuns.Id, manifest, [.. records]));

    public ValueTask<ExecutionJournalSnapshot?> ReadAsync(
        PlanFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        if (ReadOverride is not null)
        {
            return ValueTask.FromResult(ReadOverride(fingerprint));
        }

        return ValueTask.FromResult(
            _journals.TryGetValue(fingerprint, out JournalData? data)
                ? data.Snapshot()
                : null);
    }

    public async ValueTask<ExecutionJournalReadResult> ReadForReplayAsync(
        PlanFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReplayReadOverride is not null)
        {
            ReadCount++;
            return ReplayReadOverride(fingerprint);
        }

        ExecutionJournalSnapshot? snapshot = await ReadAsync(fingerprint, cancellationToken);
        return snapshot is null
            ? new ExecutionJournalReadResult(ExecutionJournalReadStatus.NotFound)
            : new ExecutionJournalReadResult(ExecutionJournalReadStatus.Loaded, snapshot);
    }

    public ValueTask<JournalBeginResult> BeginAsync(
        ExecutionManifest manifest,
        ExecutionJournalRecord startRecord,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeginCount++;
        OnBoundary?.Invoke(
            new JournalBoundary(JournalFaultBoundary.BeforeBegin, startRecord));

        JournalBeginResult? overridden = BeginOverride?.Invoke(manifest, startRecord);
        if (overridden is not null)
        {
            return ValueTask.FromResult(overridden);
        }

        if (_journals.TryGetValue(manifest.Fingerprint, out JournalData? existing))
        {
            return ValueTask.FromResult(
                new JournalBeginResult(JournalBeginStatus.AlreadyExists, existing.Snapshot()));
        }

        JournalData? blocker = _journals.Values.FirstOrDefault(
            data => IsUnresolved(data.Snapshot()));
        if (blocker is not null)
        {
            return ValueTask.FromResult(
                new JournalBeginResult(
                    JournalBeginStatus.BlockedByUnresolvedExecution,
                    blocker.Snapshot()));
        }

        JournalData created = new(startRecord.RunId, manifest, [startRecord]);
        _journals.Add(manifest.Fingerprint, created);
        OnBoundary?.Invoke(
            new JournalBoundary(JournalFaultBoundary.AfterBegin, startRecord));
        return ValueTask.FromResult(
            new JournalBeginResult(JournalBeginStatus.Begun, created.Snapshot()));
    }

    public ValueTask<JournalPublishResult> PublishAsync(
        ExecutionJournalRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishCount++;
        OnBoundary?.Invoke(
            new JournalBoundary(JournalFaultBoundary.BeforePublish, record));
        JournalPublishResult? overridden = PublishOverride?.Invoke(record);
        if (overridden is not null)
        {
            return ValueTask.FromResult(overridden);
        }

        JournalData data = _journals[record.Fingerprint];
        if (!record.RunId.Equals(data.RunId))
        {
            return ValueTask.FromResult(JournalPublishResult.SequenceConflict());
        }

        JournalPublishResult result;
        if (record.Sequence < data.Records.Count)
        {
            ExecutionJournalRecord existing = data.Records[checked((int)record.Sequence)];
            result = existing.Equals(record)
                ? JournalPublishResult.AlreadyPublished(existing)
                : JournalPublishResult.SequenceConflict(existing);
        }
        else if (record.Sequence == data.Records.Count)
        {
            data.Records.Add(record);
            result = JournalPublishResult.Published();
        }
        else
        {
            throw new InvalidOperationException("The scripted journal does not accept gaps.");
        }

        OnBoundary?.Invoke(
            new JournalBoundary(JournalFaultBoundary.AfterPublish, record));
        return ValueTask.FromResult(result);
    }

    private static bool IsUnresolved(ExecutionJournalSnapshot snapshot)
    {
        try
        {
            return !ExecutionJournalStateMachine.Replay(snapshot)
                .ReleasesGlobalExecutionOwnership;
        }
        catch (InvalidJournalSequenceException)
        {
            return true;
        }
    }

    private sealed class JournalData(
        ExecutionRunId runId,
        ExecutionManifest manifest,
        List<ExecutionJournalRecord> records)
    {
        internal ExecutionRunId RunId { get; } = runId;

        internal ExecutionManifest Manifest { get; } = manifest;

        internal List<ExecutionJournalRecord> Records { get; } = records;

        internal ExecutionJournalSnapshot Snapshot() => new(RunId, Manifest, Records);
    }
}

internal sealed class ScriptedPreflightPort : IExecutionPreflightPort
{
    internal Func<ExecutionManifest, CancellationToken, ExecutionPreflightResult>? Handler
    {
        get;
        set;
    }

    internal int CallCount { get; private set; }

    internal IGlobalExecutionLease? ExecutionLease { get; private set; }

    public ValueTask<ExecutionPreflightResult> InspectAsync(
        IGlobalExecutionLease executionLease,
        ExecutionManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionLease);
        cancellationToken.ThrowIfCancellationRequested();
        ExecutionLease = executionLease;
        CallCount++;
        ExecutionPreflightResult result = Handler?.Invoke(manifest, cancellationToken)
            ?? new ExecutionPreflightResult(
                manifest.Items.Select(item =>
                    ExecutionPreflightItemResult.Ready(item.Ordinal)));
        return ValueTask.FromResult(result);
    }
}

internal sealed class ScriptedMovePort : IMoveOperationPort, IRecoveryObservationPort
{
    private readonly Dictionary<int, ScriptedMoveStep> _steps = [];

    internal List<int> AcquiredOrdinals { get; } = [];

    internal List<int> CommittedOrdinals { get; } = [];

    internal List<int> ObservedOrdinals { get; } = [];

    internal List<int> RecoveryObservedOrdinals { get; } = [];

    internal List<IGlobalExecutionLease> AcquisitionExecutionLeases { get; } = [];

    internal List<ExecutionManifest> AcquisitionManifests { get; } = [];

    internal List<IGlobalExecutionLease> RecoveryExecutionLeases { get; } = [];

    internal ScriptedMoveStep this[int ordinal]
    {
        get
        {
            if (!_steps.TryGetValue(ordinal, out ScriptedMoveStep? step))
            {
                step = new ScriptedMoveStep(ordinal);
                _steps.Add(ordinal, step);
            }

            return step;
        }
    }

    public ValueTask<MoveLeaseAcquisition> TryAcquireAsync(
        IGlobalExecutionLease executionLease,
        ExecutionManifest manifest,
        ExecutionManifestItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionLease);
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        AcquisitionExecutionLeases.Add(executionLease);
        AcquisitionManifests.Add(manifest);
        AcquiredOrdinals.Add(item.Ordinal);
        ScriptedMoveStep step = this[item.Ordinal];
        return ValueTask.FromResult(
            step.AcquisitionProblem is null
                ? MoveLeaseAcquisition.Ready(new Lease(this, step, item))
                : step.AcquisitionReadiness == ExecutionReadiness.Conflict
                    ? MoveLeaseAcquisition.Conflict(step.AcquisitionProblem)
                    : MoveLeaseAcquisition.Blocked(step.AcquisitionProblem));
    }

    public ValueTask<MoveObservation> ObserveAsync(
        IGlobalExecutionLease executionLease,
        ExecutionManifestItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionLease);
        cancellationToken.ThrowIfCancellationRequested();
        RecoveryExecutionLeases.Add(executionLease);
        RecoveryObservedOrdinals.Add(item.Ordinal);
        ScriptedMoveStep step = this[item.Ordinal];
        MoveObservation observation = step.CurrentObservation;
        step.OnRecoveryObserved?.Invoke();
        return ValueTask.FromResult(observation);
    }

    private sealed class Lease(
        ScriptedMovePort owner,
        ScriptedMoveStep step,
        ExecutionManifestItem item) : IMoveOperationLease
    {
        public PhysicalEntryIdentity SourcePhysicalIdentity => step.Identity;

        public PhysicalEntryIdentity DestinationDirectoryPhysicalIdentity =>
            step.DestinationDirectoryIdentityOverride
            ?? item.DestinationDirectoryPhysicalIdentity;

        public ValueTask<MoveAttemptResult> CommitAsync()
        {
            owner.CommittedOrdinals.Add(step.Ordinal);
            if (step.FaultBeforeStateChange is not null)
            {
                throw step.FaultBeforeStateChange;
            }

            step.CurrentObservation = step.PostCommitObservation;
            step.OnCommitted?.Invoke();
            if (step.FaultAfterStateChange is not null)
            {
                throw step.FaultAfterStateChange;
            }

            return ValueTask.FromResult(step.AttemptResult);
        }

        public ValueTask<MoveObservation> ObserveAsync()
        {
            owner.ObservedOrdinals.Add(step.Ordinal);
            return ValueTask.FromResult(step.CurrentObservation);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class ScriptedMoveStep
{
    internal ScriptedMoveStep(int ordinal)
    {
        Ordinal = ordinal;
        Identity = TestPhysicalIdentities.File($"entry-{ordinal}");
        CurrentObservation = SourceOnly(Identity);
        PostCommitObservation = TargetOnly(Identity);
        AttemptResult = MoveAttemptResult.Success();
    }

    internal int Ordinal { get; }

    internal PhysicalEntryIdentity Identity { get; set; }

    internal PhysicalEntryIdentity? DestinationDirectoryIdentityOverride { get; set; }

    internal ExecutionReadiness AcquisitionReadiness { get; set; } =
        ExecutionReadiness.Ready;

    internal ExecutionProblem? AcquisitionProblem { get; set; }

    internal MoveAttemptResult AttemptResult { get; set; }

    internal MoveObservation CurrentObservation { get; set; }

    internal MoveObservation PostCommitObservation { get; set; }

    internal Exception? FaultBeforeStateChange { get; set; }

    internal Exception? FaultAfterStateChange { get; set; }

    internal Action? OnCommitted { get; set; }

    internal Action? OnRecoveryObserved { get; set; }

    internal static MoveObservation SourceOnly(PhysicalEntryIdentity identity) => new(
        RecoveryEntryObservation.Present(identity),
        RecoveryEntryObservation.Missing());

    internal static MoveObservation TargetOnly(PhysicalEntryIdentity identity) => new(
        RecoveryEntryObservation.Missing(),
        RecoveryEntryObservation.Present(identity));
}

internal sealed class SimulatedCrashException(string message) : Exception(message);
