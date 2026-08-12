using System.Globalization;
using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Application.Orchestration;
using FileOrganizer.Infrastructure.Journaling;

namespace FileOrganizer.Infrastructure.Tests.Journaling;

public sealed class DurableJournalReplayIntegrationTests
{
    private static readonly ExecutionRunId RunId =
        new("33333333333333333333333333333333");
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 12, 14, 0, 0, TimeSpan.Zero);

    public static TheoryData<ReplayHistory, ExecutionRecoveryOutcome, ExecutionJournalStatus>
        ValidHistories => new()
        {
            { ReplayHistory.GenesisOnly, ExecutionRecoveryOutcome.Resumable, ExecutionJournalStatus.InProgress },
            { ReplayHistory.IntentOnly, ExecutionRecoveryOutcome.RecoveryRequired, ExecutionJournalStatus.AwaitingRecovery },
            { ReplayHistory.MoveApplied, ExecutionRecoveryOutcome.Resumable, ExecutionJournalStatus.InProgress },
            { ReplayHistory.MoveNotApplied, ExecutionRecoveryOutcome.Resumable, ExecutionJournalStatus.Resumable },
            { ReplayHistory.MoveAmbiguous, ExecutionRecoveryOutcome.Ambiguous, ExecutionJournalStatus.ManualRecovery },
            { ReplayHistory.Interrupted, ExecutionRecoveryOutcome.Interrupted, ExecutionJournalStatus.Interrupted },
            { ReplayHistory.Completed, ExecutionRecoveryOutcome.Completed, ExecutionJournalStatus.Completed },
        };

    [Theory]
    [MemberData(nameof(ValidHistories))]
    public async Task RealDurableHistoriesReplayThroughApplicationReducer(
        ReplayHistory history,
        ExecutionRecoveryOutcome expectedOutcome,
        ExecutionJournalStatus expectedStatus)
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        FileExecutionJournalStore store = new(Path.Combine(temp.FullPath, "journal"));
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        await PublishHistoryAsync(store, manifest, history);
        ExecutionJournalRecoveryInspector inspector = new(store);

        ExecutionRecoveryResult result = await inspector.InspectAsync(
            manifest.Fingerprint,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedStatus, Assert.IsType<ExecutionJournalState>(result.JournalState).Status);
        Assert.Equal(manifest.Fingerprint, result.Manifest!.Fingerprint);
    }

    [Fact]
    public async Task UnchangedDurableJournalReplaysEquivalentlyAcrossCultureAndRepeatedReads()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        await PublishHistoryAsync(store, manifest, ReplayHistory.Completed);
        ExecutionJournalRecoveryInspector inspector = new(store);
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            ExecutionRecoveryResult first = await inspector.InspectAsync(
                manifest.Fingerprint,
                TestContext.Current.CancellationToken);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            ExecutionRecoveryResult second = await inspector.InspectAsync(
                manifest.Fingerprint,
                TestContext.Current.CancellationToken);

            Assert.Equal(first.Outcome, second.Outcome);
            Assert.Equal(first.Manifest!.Fingerprint, second.Manifest!.Fingerprint);
            Assert.Equal(first.JournalState!.Status, second.JournalState!.Status);
            Assert.Equal(first.JournalState.NextSequence, second.JournalState.NextSequence);
            Assert.Equal(
                first.JournalState.Operations.Select(OperationShape),
                second.JournalState.Operations.Select(OperationShape));
            Assert.Equal(first.JournalProblems, second.JournalProblems);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public async Task CorruptBytesReachApplicationAsManualInvalidStateWithoutSnapshot()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        await PublishHistoryAsync(store, manifest, ReplayHistory.GenesisOnly);
        File.WriteAllText(
            Path.Combine(root, manifest.Fingerprint.Value, "manifest.json"),
            "not-json");
        ExecutionJournalRecoveryInspector inspector = new(store);

        ExecutionRecoveryResult result = await inspector.InspectAsync(
            manifest.Fingerprint,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionRecoveryOutcome.InvalidJournal, result.Outcome);
        Assert.Null(result.Manifest);
        Assert.Null(result.JournalState);
        Assert.Equal(
            ExecutionJournalReadProblemKind.ManifestMalformed,
            Assert.Single(result.JournalProblems).Kind);
        Assert.Equal(ExecutionProblemKind.InvalidJournal, Assert.Single(result.Problems).Kind);
    }

    private static string OperationShape(ExecutionJournalOperationState operation) =>
        FormattableString.Invariant(
            $"{operation.Ordinal}:{operation.Status}:{operation.LastRecordSequence}:{operation.Reason}");

    private static async Task PublishHistoryAsync(
        FileExecutionJournalStore store,
        ExecutionManifest manifest,
        ReplayHistory history)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ExecutionJournalRecord start = ExecutionJournalRecord.ExecutionStarted(
            RunId,
            manifest.Fingerprint,
            0,
            StartedAt);
        Assert.Equal(
            JournalBeginStatus.Begun,
            (await store.BeginAsync(manifest, start, token)).Status);
        if (history == ReplayHistory.GenesisOnly)
        {
            return;
        }

        if (history == ReplayHistory.Interrupted)
        {
            await PublishAsync(
                store,
                ExecutionJournalRecord.ExecutionInterrupted(
                    RunId,
                    manifest.Fingerprint,
                    1,
                    StartedAt.AddSeconds(1),
                    JournalRecordReasonKind.ExecutionFault));
            return;
        }

        await PublishAsync(
            store,
            ExecutionJournalRecord.MoveIntent(
                RunId,
                manifest.Fingerprint,
                1,
                StartedAt.AddSeconds(1),
                0,
                manifest.Items[0].SourcePrecondition.PhysicalIdentity!));
        if (history == ReplayHistory.IntentOnly)
        {
            return;
        }

        ExecutionJournalRecord outcome = history switch
        {
            ReplayHistory.MoveNotApplied => ExecutionJournalRecord.MoveNotApplied(
                RunId,
                manifest.Fingerprint,
                2,
                StartedAt.AddSeconds(2),
                0,
                JournalRecordReasonKind.MoveDidNotApply),
            ReplayHistory.MoveAmbiguous => ExecutionJournalRecord.MoveAmbiguous(
                RunId,
                manifest.Fingerprint,
                2,
                StartedAt.AddSeconds(2),
                0,
                JournalRecordReasonKind.MoveOutcomeUnknown),
            _ => ExecutionJournalRecord.MoveApplied(
                RunId,
                manifest.Fingerprint,
                2,
                StartedAt.AddSeconds(2),
                0),
        };
        await PublishAsync(store, outcome);
        if (history == ReplayHistory.Completed)
        {
            await PublishAsync(
                store,
                ExecutionJournalRecord.ExecutionCompleted(
                    RunId,
                    manifest.Fingerprint,
                    3,
                    StartedAt.AddSeconds(3)));
        }
    }

    private static async Task PublishAsync(
        FileExecutionJournalStore store,
        ExecutionJournalRecord record) =>
        Assert.Equal(
            JournalPublishStatus.Published,
            (await store.PublishAsync(record, TestContext.Current.CancellationToken)).Status);

    public enum ReplayHistory
    {
        GenesisOnly,
        IntentOnly,
        MoveApplied,
        MoveNotApplied,
        MoveAmbiguous,
        Interrupted,
        Completed,
    }
}
