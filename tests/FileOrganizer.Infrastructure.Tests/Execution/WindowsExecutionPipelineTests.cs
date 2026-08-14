using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Application.Orchestration;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;
using FileOrganizer.Infrastructure.Execution;
using FileOrganizer.Infrastructure.FileSystem;
using FileOrganizer.Infrastructure.Journaling;
using FileOrganizer.Infrastructure.Tests.FileSystem;
using FileOrganizer.Infrastructure.Tests.Journaling;

namespace FileOrganizer.Infrastructure.Tests.Execution;

[Collection(WindowsGlobalExecutionLeaseTestGroup.Name)]
public sealed class WindowsExecutionPipelineTests
{
    [Fact]
    public async Task ExecutesTwoMovesDurablyThenReplaysWithoutASecondMutation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native execution test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        string journalRoot = Path.Combine(temporary.FullPath, "journal");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string secondSource = temporary.WriteFile(
            Path.Combine("source", "zeta.txt"),
            "second");
        string firstSource = temporary.WriteFile(
            Path.Combine("source", "alpha.txt"),
            "first");
        OrganizationPlan plan = WindowsPhysicalMoveTestPlan.CreatePlan(
            sourceRoot,
            [secondSource, firstSource],
            destination);

        OrganizationPlanExecutor firstExecutor = WindowsExecutionPipeline.Create(journalRoot);
        ExecutionResult first = await firstExecutor.ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionOutcome.Completed, first.Outcome);
        Assert.False(first.WasReplayed);
        Assert.Equal(
            [ExecutionItemOutcome.Applied, ExecutionItemOutcome.Applied],
            first.Items.Select(static item => item.Outcome));
        Assert.Equal(
            ["alpha.txt", "zeta.txt"],
            first.Manifest!.Items.Select(
                static item => item.SourcePrecondition.CanonicalPath.FileName));

        FileExecutionJournalStore journal = new(journalRoot);
        ExecutionJournalSnapshot snapshot = Assert.IsType<ExecutionJournalSnapshot>(
            await journal.ReadAsync(
                first.Fingerprint!,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            [
                ExecutionJournalRecordKind.ExecutionStarted,
                ExecutionJournalRecordKind.MoveIntent,
                ExecutionJournalRecordKind.MoveApplied,
                ExecutionJournalRecordKind.MoveIntent,
                ExecutionJournalRecordKind.MoveApplied,
                ExecutionJournalRecordKind.ExecutionCompleted,
            ],
            snapshot.Records.Select(static record => record.Kind));
        Assert.Equal(
            [null, 0, 0, 1, 1, null],
            snapshot.Records.Select(static record => record.OperationOrdinal));

        var observer = new WindowsPhysicalIdentityAdapter();
        PhysicalEntryIdentity[] targetIdentities = new PhysicalEntryIdentity[2];
        for (int ordinal = 0; ordinal < first.Manifest.Items.Count; ordinal++)
        {
            ExecutionManifestItem item = first.Manifest.Items[ordinal];
            Assert.False(File.Exists(item.SourcePrecondition.CanonicalPath.Value));
            PhysicalEntryObservation target = observer.Observe(
                item.TargetPath,
                TestContext.Current.CancellationToken);
            Assert.Equal(PhysicalEntryObservationOutcome.Success, target.Outcome);
            Assert.Equal(item.SourcePrecondition.PhysicalIdentity, target.Identity);
            targetIdentities[ordinal] = target.Identity!;
        }

        OrganizationPlanExecutor replayExecutor = WindowsExecutionPipeline.Create(journalRoot);
        ExecutionResult replay = await replayExecutor.ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionOutcome.Completed, replay.Outcome);
        Assert.True(replay.WasReplayed);
        ExecutionJournalSnapshot replayedSnapshot = Assert.IsType<ExecutionJournalSnapshot>(
            await new FileExecutionJournalStore(journalRoot).ReadAsync(
                replay.Fingerprint!,
                TestContext.Current.CancellationToken));
        Assert.Equal(snapshot.Records, replayedSnapshot.Records);
        for (int ordinal = 0; ordinal < replay.Manifest!.Items.Count; ordinal++)
        {
            ExecutionManifestItem item = replay.Manifest.Items[ordinal];
            Assert.False(File.Exists(item.SourcePrecondition.CanonicalPath.Value));
            Assert.Equal(
                targetIdentities[ordinal],
                observer.Observe(
                    item.TargetPath,
                    TestContext.Current.CancellationToken).Identity);
        }
    }

    [Fact]
    public async Task SourceRootJournalOverlapFailsBeforeGenesisOrNativeMove()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native execution test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string destination = Path.Combine(temporary.FullPath, "destination");
        string journalRoot = Path.Combine(sourceRoot, "journal");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destination);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "source");
        OrganizationPlan plan = WindowsPhysicalMoveTestPlan.CreatePlan(
            sourceRoot,
            [source],
            destination);

        ExecutionResult result = await WindowsExecutionPipeline.Create(journalRoot).ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionOutcome.Stopped, result.Outcome);
        ExecutionItemResult item = Assert.Single(result.Items);
        Assert.Equal(ExecutionItemOutcome.Blocked, item.Outcome);
        ExecutionProblem problem = Assert.Single(item.Problems);
        Assert.Equal(ExecutionProblemKind.SourceRevalidationFailed, problem.Kind);
        Assert.Contains("journal namespace", problem.Description, StringComparison.Ordinal);
        Assert.False(Directory.Exists(journalRoot));
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(destination, "report.txt")));
    }

    [Fact]
    public async Task DestinationJournalOverlapFailsBeforeGenesisOrNativeMove()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native execution test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string journalRoot = Path.Combine(temporary.FullPath, "journal");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(journalRoot);
        string source = temporary.WriteFile(Path.Combine("source", "report.txt"), "source");
        OrganizationPlan plan = WindowsPhysicalMoveTestPlan.CreatePlan(
            sourceRoot,
            [source],
            journalRoot);

        ExecutionResult result = await WindowsExecutionPipeline.Create(journalRoot).ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionOutcome.Stopped, result.Outcome);
        ExecutionItemResult item = Assert.Single(result.Items);
        Assert.Equal(ExecutionItemOutcome.Blocked, item.Outcome);
        ExecutionProblem problem = Assert.Single(item.Problems);
        Assert.Equal(ExecutionProblemKind.DestinationRevalidationFailed, problem.Kind);
        Assert.Contains("journal namespace", problem.Description, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(journalRoot));
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(journalRoot, "report.txt")));
    }

    [Fact]
    public async Task TargetJournalOverlapFailsBeforeGenesisOrNativeMove()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows native execution test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temporary.FullPath, "source");
        string journalRoot = Path.Combine(temporary.FullPath, "journal.txt");
        Directory.CreateDirectory(sourceRoot);
        string source = temporary.WriteFile(Path.Combine("source", "journal.txt"), "source");
        OrganizationPlan plan = WindowsPhysicalMoveTestPlan.CreatePlan(
            sourceRoot,
            [source],
            temporary.FullPath);

        ExecutionResult result = await WindowsExecutionPipeline.Create(journalRoot).ExecuteAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionOutcome.Stopped, result.Outcome);
        ExecutionItemResult item = Assert.Single(result.Items);
        Assert.Equal(ExecutionItemOutcome.Blocked, item.Outcome);
        ExecutionProblem problem = Assert.Single(item.Problems);
        Assert.Equal(ExecutionProblemKind.DestinationRevalidationFailed, problem.Kind);
        Assert.Contains("journal namespace", problem.Description, StringComparison.Ordinal);
        Assert.False(Directory.Exists(journalRoot));
        Assert.True(File.Exists(source));
    }
}
