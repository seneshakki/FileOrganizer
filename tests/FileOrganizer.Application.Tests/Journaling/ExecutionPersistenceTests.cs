using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Application.Tests.Execution;
using FileOrganizer.Core.Actions;

namespace FileOrganizer.Application.Tests.Journaling;

public sealed class ExecutionPersistenceTests
{
    [Fact]
    public void RunIdentifierRequiresExactLowercaseHexAndRandomCreationHasTheSameShape()
    {
        ExecutionRunId exact = new("abcdef0123456789abcdef0123456789");
        ExecutionRunId created = ExecutionRunId.CreateNew();

        Assert.Equal("abcdef0123456789abcdef0123456789", exact.Value);
        Assert.Equal(ExecutionRunId.HexLength, created.Value.Length);
        Assert.All(
            created.Value,
            value => Assert.True(value is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.Throws<ArgumentException>(
            () => new ExecutionRunId("ABCDEF0123456789ABCDEF0123456789"));
        Assert.Throws<ArgumentException>(() => new ExecutionRunId("../escape"));
        Assert.Throws<ArgumentException>(
            () => new ExecutionRunId("gggggggggggggggggggggggggggggggg"));
    }

    [Fact]
    public void ManifestRestorationRoundTripsAndCopiesCallerItems()
    {
        ExecutionManifest original = JournalTestManifest.Create(operationCount: 2);
        List<ExecutionManifestPersistenceItem> persisted = original.Items
            .Select(ToPersistenceItem)
            .Reverse()
            .ToList();

        ExecutionManifest restored = ExecutionManifestPersistence.RestoreManifest(
            original.Fingerprint,
            original.GeneratedAtUtc,
            original.PathSemantics,
            original.SourceRoot.Value,
            original.SourceRootPhysicalIdentity,
            persisted);
        persisted.Clear();

        Assert.Equal(original.Fingerprint, restored.Fingerprint);
        Assert.Equal(original.GeneratedAtUtc, restored.GeneratedAtUtc);
        Assert.Equal(original.SourceRoot.Value, restored.SourceRoot.Value);
        Assert.Equal([0, 1], restored.Items.Select(item => item.Ordinal));
        Assert.Equal(
            original.Items.Select(item => item.TargetPath.Value),
            restored.Items.Select(item => item.TargetPath.Value));
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionManifestItem>)restored.Items).Clear());
    }

    [Fact]
    public void ManifestRestorationRejectsFingerprintMismatchAndUnsafeRelationships()
    {
        ExecutionManifest original = JournalTestManifest.Create();
        ExecutionManifestPersistenceItem item = ToPersistenceItem(original.Items[0]);
        PlanFingerprint wrongFingerprint = new(new string('a', PlanFingerprint.HexLength));
        if (wrongFingerprint.Equals(original.Fingerprint))
        {
            wrongFingerprint = new PlanFingerprint(new string('b', PlanFingerprint.HexLength));
        }

        Assert.Throws<ArgumentException>(
            () => ExecutionManifestPersistence.RestoreManifest(
                wrongFingerprint,
                original.GeneratedAtUtc,
                original.PathSemantics,
                original.SourceRoot.Value,
                original.SourceRootPhysicalIdentity,
                [item]));

        ExecutionManifestPersistenceItem escapedSource = new(
            item.Ordinal,
            item.ActionKind,
            @"C:\Outside\source.txt",
            item.SourcePhysicalIdentity,
            item.SourceLength,
            item.SourceCreatedUtc,
            item.SourceModifiedUtc,
            item.DestinationDirectory,
            item.DestinationDirectoryPhysicalIdentity,
            item.TargetPath,
            item.SelectedRuleName,
            item.SelectedRuleOrder);
        Assert.Throws<ArgumentException>(
            () => ExecutionManifestPersistence.RestoreManifest(
                original.Fingerprint,
                original.GeneratedAtUtc,
                original.PathSemantics,
                original.SourceRoot.Value,
                original.SourceRootPhysicalIdentity,
                [escapedSource]));
    }

    [Fact]
    public void RecordRestorationValidatesShapeAndSnapshotRejectsMixedRunOwnership()
    {
        ExecutionManifest manifest = JournalTestManifest.Create();
        ExecutionJournalRecord start = ExecutionJournalPersistence.RestoreRecord(
            TestExecutionRuns.Id,
            manifest.Fingerprint,
            0,
            manifest.GeneratedAtUtc,
            ExecutionJournalRecordKind.ExecutionStarted,
            operationOrdinal: null,
            expectedEntryIdentity: null,
            JournalRecordReasonKind.None);

        Assert.Equal(TestExecutionRuns.Id, start.RunId);
        Assert.Equal(ExecutionJournalRecordKind.ExecutionStarted, start.Kind);
        Assert.Throws<ArgumentException>(
            () => ExecutionJournalPersistence.RestoreRecord(
                TestExecutionRuns.Id,
                manifest.Fingerprint,
                1,
                manifest.GeneratedAtUtc,
                ExecutionJournalRecordKind.MoveIntent,
                operationOrdinal: 0,
                expectedEntryIdentity: null,
                JournalRecordReasonKind.None));

        ExecutionJournalRecord differentRun = ExecutionJournalRecord.ExecutionStarted(
            new ExecutionRunId("11111111111111111111111111111111"),
            manifest.Fingerprint,
            0,
            manifest.GeneratedAtUtc);
        Assert.Throws<ArgumentException>(
            () => new ExecutionJournalSnapshot(TestExecutionRuns.Id, manifest, [differentRun]));
    }

    private static ExecutionManifestPersistenceItem ToPersistenceItem(
        ExecutionManifestItem item) =>
        new(
            item.Ordinal,
            OrganizationActionKind.MoveToDirectory,
            item.SourcePrecondition.CanonicalPath.Value,
            item.SourcePrecondition.PhysicalIdentity!,
            item.SourcePrecondition.Length,
            item.SourcePrecondition.CreatedUtc,
            item.SourcePrecondition.ModifiedUtc,
            item.DestinationDirectory.Value,
            item.DestinationDirectoryPhysicalIdentity,
            item.TargetPath.Value,
            item.SelectedRuleName,
            item.SelectedRuleOrder);
}
