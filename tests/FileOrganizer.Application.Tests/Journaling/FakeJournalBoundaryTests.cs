using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Tests.Execution;
using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Application.Tests.Journaling;

public sealed class FakeJournalBoundaryTests
{
    private static readonly ExecutionRunId RunId =
        new("0123456789abcdef0123456789abcdef");

    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BeginAtomicallyStoresTheCompleteManifestAndStartRecord()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalRecord start = ExecutionJournalRecord.ExecutionStarted(
            RunId,
            manifest.Fingerprint,
            0,
            StartedAt);
        FakeExecutionJournalStore store = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        JournalBeginResult first = await store.BeginAsync(manifest, start, cancellationToken);
        JournalBeginResult second = await store.BeginAsync(manifest, start, cancellationToken);

        Assert.Equal(JournalBeginStatus.Begun, first.Status);
        Assert.Equal(JournalBeginStatus.AlreadyExists, second.Status);
        Assert.Same(manifest, first.Snapshot.Manifest);
        Assert.Same(manifest, second.Snapshot.Manifest);
        Assert.Equal(start, Assert.Single(second.Snapshot.Records));
    }

    [Fact]
    public async Task PublishIsIdempotentAndRejectsConflictingOrGappedSequences()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        FakeExecutionJournalStore store = new();
        ExecutionJournalRecord start = ExecutionJournalRecord.ExecutionStarted(
            RunId,
            manifest.Fingerprint,
            0,
            StartedAt);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.BeginAsync(manifest, start, cancellationToken);
        ExecutionJournalRecord intent = ExecutionJournalRecord.MoveIntent(
            RunId,
            manifest.Fingerprint,
            1,
            StartedAt.AddSeconds(1),
            0,
            TestPhysicalIdentities.File("entry", "volume", "test-id-v1"));

        JournalPublishResult published = await store.PublishAsync(intent, cancellationToken);
        JournalPublishResult repeated = await store.PublishAsync(intent, cancellationToken);
        JournalPublishResult conflicting = await store.PublishAsync(
            ExecutionJournalRecord.ExecutionInterrupted(
                RunId,
                manifest.Fingerprint,
                1,
                StartedAt.AddSeconds(1),
                JournalRecordReasonKind.ExecutionFault),
            cancellationToken);
        JournalPublishResult gap = await store.PublishAsync(
            ExecutionJournalRecord.MoveApplied(
                RunId,
                manifest.Fingerprint,
                3,
                StartedAt.AddSeconds(3),
                0),
            cancellationToken);

        Assert.Equal(JournalPublishStatus.Published, published.Status);
        Assert.Equal(JournalPublishStatus.AlreadyPublished, repeated.Status);
        Assert.Equal(intent, repeated.ExistingRecord);
        Assert.Equal(JournalPublishStatus.SequenceConflict, conflicting.Status);
        Assert.Equal(intent, conflicting.ExistingRecord);
        Assert.Equal(JournalPublishStatus.SequenceConflict, gap.Status);
        Assert.Null(gap.ExistingRecord);
    }

    [Fact]
    public async Task DifferentFingerprintIsBlockedUntilTheDurableOwnerIsSafelyClosed()
    {
        FakeExecutionJournalStore store = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ExecutionManifest first = JournalTestManifest.Create(pathToken: "first");
        ExecutionManifest second = JournalTestManifest.Create(pathToken: "second");
        ExecutionManifest active = JournalTestManifest.Create(pathToken: "active");
        ExecutionManifest blocked = JournalTestManifest.Create(pathToken: "blocked");

        JournalBeginResult firstBegin = await BeginAsync(store, first, cancellationToken);
        JournalBeginResult blockedByFirst = await BeginAsync(store, second, cancellationToken);

        Assert.Equal(JournalBeginStatus.Begun, firstBegin.Status);
        Assert.Equal(
            JournalBeginStatus.BlockedByUnresolvedExecution,
            blockedByFirst.Status);
        Assert.Same(first, blockedByFirst.Snapshot.Manifest);
        Assert.Null(await store.ReadAsync(second.Fingerprint, cancellationToken));

        await CompleteAsync(store, first, cancellationToken);
        Assert.Equal(
            JournalBeginStatus.Begun,
            (await BeginAsync(store, second, cancellationToken)).Status);
        await CompleteAsync(store, second, cancellationToken);
        Assert.Equal(
            JournalBeginStatus.Begun,
            (await BeginAsync(store, active, cancellationToken)).Status);

        JournalBeginResult blockedByActive = await BeginAsync(
            store,
            blocked,
            cancellationToken);

        Assert.Equal(
            JournalBeginStatus.BlockedByUnresolvedExecution,
            blockedByActive.Status);
        Assert.Same(active, blockedByActive.Snapshot.Manifest);
        Assert.NotNull(await store.ReadAsync(first.Fingerprint, cancellationToken));
        Assert.NotNull(await store.ReadAsync(second.Fingerprint, cancellationToken));
        Assert.NotNull(await store.ReadAsync(active.Fingerprint, cancellationToken));
        Assert.Null(await store.ReadAsync(blocked.Fingerprint, cancellationToken));
    }

    [Fact]
    public async Task GlobalLeaseRejectsASecondOwnerRegardlessOfFingerprint()
    {
        FakeGlobalExecutionLeaseProvider provider = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        IGlobalExecutionLease? first = await provider.TryAcquireAsync(cancellationToken);
        IGlobalExecutionLease? second = await provider.TryAcquireAsync(cancellationToken);

        Assert.NotNull(first);
        Assert.Null(second);

        await first.DisposeAsync();
        IGlobalExecutionLease? afterRelease = await provider.TryAcquireAsync(cancellationToken);
        Assert.NotNull(afterRelease);
        await afterRelease.DisposeAsync();
    }

    private static ValueTask<JournalBeginResult> BeginAsync(
        IExecutionJournalStore store,
        ExecutionManifest manifest,
        CancellationToken cancellationToken) => store.BeginAsync(
            manifest,
            ExecutionJournalRecord.ExecutionStarted(
                RunId,
                manifest.Fingerprint,
                0,
                StartedAt),
            cancellationToken);

    private static async ValueTask CompleteAsync(
        IExecutionJournalStore store,
        ExecutionManifest manifest,
        CancellationToken cancellationToken)
    {
        long sequence = 1;
        foreach (ExecutionManifestItem item in manifest.Items)
        {
            JournalPublishResult intent = await store.PublishAsync(
                ExecutionJournalRecord.MoveIntent(
                    RunId,
                    manifest.Fingerprint,
                    sequence,
                    StartedAt.AddSeconds(sequence),
                    item.Ordinal,
                    TestPhysicalIdentities.File(
                        $"entry-{item.Ordinal}",
                        "volume",
                        "test-id-v1")),
                cancellationToken);
            Assert.Equal(JournalPublishStatus.Published, intent.Status);
            sequence++;

            JournalPublishResult applied = await store.PublishAsync(
                ExecutionJournalRecord.MoveApplied(
                    RunId,
                    manifest.Fingerprint,
                    sequence,
                    StartedAt.AddSeconds(sequence),
                    item.Ordinal),
                cancellationToken);
            Assert.Equal(JournalPublishStatus.Published, applied.Status);
            sequence++;
        }

        JournalPublishResult completed = await store.PublishAsync(
            ExecutionJournalRecord.ExecutionCompleted(
                RunId,
                manifest.Fingerprint,
                sequence,
                StartedAt.AddSeconds(sequence)),
            cancellationToken);
        Assert.Equal(JournalPublishStatus.Published, completed.Status);
    }

    private sealed class FakeExecutionJournalStore : IExecutionJournalStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<PlanFingerprint, JournalData> _journals = [];

        public ValueTask<ExecutionJournalSnapshot?> ReadAsync(
            PlanFingerprint fingerprint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTask.FromResult(
                    _journals.TryGetValue(fingerprint, out JournalData? journal)
                        ? journal.Snapshot()
                        : null);
            }
        }

        public ValueTask<JournalBeginResult> BeginAsync(
            ExecutionManifest manifest,
            ExecutionJournalRecord startRecord,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(manifest);
            ArgumentNullException.ThrowIfNull(startRecord);

            if (startRecord.Kind != ExecutionJournalRecordKind.ExecutionStarted
                || startRecord.Sequence != 0
                || !startRecord.Fingerprint.Equals(manifest.Fingerprint))
            {
                throw new ArgumentException(
                    "Begin requires the manifest's sequence-zero start record.",
                    nameof(startRecord));
            }

            lock (_gate)
            {
                if (_journals.TryGetValue(manifest.Fingerprint, out JournalData? existing))
                {
                    return ValueTask.FromResult(
                        new JournalBeginResult(
                            JournalBeginStatus.AlreadyExists,
                            existing.Snapshot()));
                }

                JournalData? unresolved = _journals.Values.FirstOrDefault(
                    static journal => !ReleasesGlobalOwnership(journal));
                if (unresolved is not null)
                {
                    return ValueTask.FromResult(
                        new JournalBeginResult(
                            JournalBeginStatus.BlockedByUnresolvedExecution,
                            unresolved.Snapshot()));
                }

                JournalData created = new(startRecord.RunId, manifest, [startRecord]);
                _journals.Add(manifest.Fingerprint, created);
                return ValueTask.FromResult(
                    new JournalBeginResult(JournalBeginStatus.Begun, created.Snapshot()));
            }
        }

        public ValueTask<JournalPublishResult> PublishAsync(
            ExecutionJournalRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(record);

            lock (_gate)
            {
                if (!_journals.TryGetValue(record.Fingerprint, out JournalData? journal))
                {
                    return ValueTask.FromResult(JournalPublishResult.SequenceConflict());
                }

                if (!record.RunId.Equals(journal.RunId))
                {
                    return ValueTask.FromResult(JournalPublishResult.SequenceConflict());
                }

                if (record.Sequence < journal.Records.Count)
                {
                    ExecutionJournalRecord existing = journal.Records[(int)record.Sequence];
                    return ValueTask.FromResult(
                        existing.Equals(record)
                            ? JournalPublishResult.AlreadyPublished(existing)
                            : JournalPublishResult.SequenceConflict(existing));
                }

                if (record.Sequence != journal.Records.Count)
                {
                    return ValueTask.FromResult(JournalPublishResult.SequenceConflict());
                }

                journal.Records.Add(record);
                return ValueTask.FromResult(JournalPublishResult.Published());
            }
        }

        private static bool ReleasesGlobalOwnership(JournalData journal)
        {
            try
            {
                return ExecutionJournalStateMachine.Replay(journal.Snapshot())
                    .ReleasesGlobalExecutionOwnership;
            }
            catch (InvalidJournalSequenceException)
            {
                return false;
            }
        }

        private sealed class JournalData(
            ExecutionRunId runId,
            ExecutionManifest manifest,
            IEnumerable<ExecutionJournalRecord> records)
        {
            public ExecutionRunId RunId { get; } = runId;

            public ExecutionManifest Manifest { get; } = manifest;

            public List<ExecutionJournalRecord> Records { get; } = [.. records];

            public ExecutionJournalSnapshot Snapshot() => new(RunId, Manifest, Records);
        }
    }

    private sealed class FakeGlobalExecutionLeaseProvider : IGlobalExecutionLeaseProvider
    {
        private int _isOwned;

        public ValueTask<IGlobalExecutionLease?> TryAcquireAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IGlobalExecutionLease? lease = Interlocked.CompareExchange(
                ref _isOwned,
                1,
                0) == 0
                ? new Lease(this)
                : null;
            return ValueTask.FromResult(lease);
        }

        private sealed class Lease(FakeGlobalExecutionLeaseProvider owner) : IGlobalExecutionLease
        {
            private FakeGlobalExecutionLeaseProvider? _owner = owner;

            public ValueTask DisposeAsync()
            {
                FakeGlobalExecutionLeaseProvider? current = Interlocked.Exchange(
                    ref _owner,
                    null);
                if (current is not null)
                {
                    Volatile.Write(ref current._isOwned, 0);
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
