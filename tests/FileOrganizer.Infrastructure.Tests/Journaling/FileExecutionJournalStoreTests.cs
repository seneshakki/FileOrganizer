using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Infrastructure.Journaling;

namespace FileOrganizer.Infrastructure.Tests.Journaling;

public sealed class FileExecutionJournalStoreTests
{
    private static readonly ExecutionRunId RunId =
        new("11111111111111111111111111111111");
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 12, 10, 1, 0, TimeSpan.Zero);

    [Fact]
    public async Task BeginsAndRoundTripsManifestAndSequentialRecords()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        ExecutionJournalRecord start = Start(manifest);

        CancellationToken token = TestContext.Current.CancellationToken;
        JournalBeginResult begin = await store.BeginAsync(manifest, start, token);
        ExecutionJournalRecord intent = ExecutionJournalRecord.MoveIntent(
            RunId,
            manifest.Fingerprint,
            1,
            StartedAt.AddSeconds(1),
            0,
            manifest.Items[0].SourcePrecondition.PhysicalIdentity!);
        Assert.Equal(JournalPublishStatus.Published, (await store.PublishAsync(intent, token)).Status);
        ExecutionJournalRecord stopped = ExecutionJournalRecord.MoveNotApplied(
            RunId,
            manifest.Fingerprint,
            2,
            StartedAt.AddSeconds(2),
            0,
            JournalRecordReasonKind.MoveDidNotApply);
        Assert.Equal(JournalPublishStatus.Published, (await store.PublishAsync(stopped, token)).Status);

        ExecutionJournalSnapshot snapshot = Assert.IsType<ExecutionJournalSnapshot>(
            await store.ReadAsync(manifest.Fingerprint, token));
        Assert.Equal(JournalBeginStatus.Begun, begin.Status);
        Assert.Equal(RunId, snapshot.RunId);
        Assert.Equal([start, intent, stopped], snapshot.Records);
        Assert.Equal(manifest.Fingerprint, snapshot.Manifest.Fingerprint);
        Assert.Equal(
            ["00000000000000000001.record.json", "00000000000000000002.record.json", "manifest.json"],
            Directory.GetFiles(Path.Combine(root, manifest.Fingerprint.Value))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task IdenticalBeginAndPublishAreIdempotentButConflictsNeverOverwrite()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        FileExecutionJournalStore store = new(Path.Combine(temp.FullPath, "journal"));
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        ExecutionJournalRecord start = Start(manifest);
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.Equal(JournalBeginStatus.Begun, (await store.BeginAsync(manifest, start, token)).Status);
        Assert.Equal(JournalBeginStatus.AlreadyExists, (await store.BeginAsync(manifest, start, token)).Status);

        ExecutionJournalRecord intent = Intent(manifest, StartedAt.AddSeconds(1));
        Assert.Equal(JournalPublishStatus.Published, (await store.PublishAsync(intent, token)).Status);
        Assert.Equal(JournalPublishStatus.AlreadyPublished, (await store.PublishAsync(intent, token)).Status);
        ExecutionJournalRecord changed = Intent(manifest, StartedAt.AddSeconds(2));
        Assert.Equal(JournalPublishStatus.SequenceConflict, (await store.PublishAsync(changed, token)).Status);
        Assert.Equal(intent, (await store.ReadAsync(manifest.Fingerprint, token))!.Records[1]);
    }

    [Fact]
    public async Task DifferentRunAndConflictingAuditContentReturnExistingWithoutOverwrite()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest original = JournalStoreTestManifest.Create();
        ExecutionManifest changed = JournalStoreTestManifest.Create(
            generatedAt: original.GeneratedAtUtc.AddMinutes(1),
            ruleName: "changed-audit-name");
        Assert.Equal(original.Fingerprint, changed.Fingerprint);
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.BeginAsync(original, Start(original), token);
        string manifestPath = Path.Combine(root, original.Fingerprint.Value, "manifest.json");
        byte[] before = File.ReadAllBytes(manifestPath);

        ExecutionRunId differentRun = new("22222222222222222222222222222222");
        ExecutionJournalRecord differentStart = ExecutionJournalRecord.ExecutionStarted(
            differentRun,
            changed.Fingerprint,
            0,
            StartedAt.AddSeconds(1));
        JournalBeginResult result = await store.BeginAsync(changed, differentStart, token);

        Assert.Equal(JournalBeginStatus.AlreadyExists, result.Status);
        Assert.Equal(RunId, result.Snapshot.RunId);
        Assert.Equal(before, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public async Task UnpublishedTemporaryFileIsReportedButIgnoredForValidatedReplay()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.BeginAsync(manifest, Start(manifest), token);
        string executionDirectory = Path.Combine(root, manifest.Fingerprint.Value);
        File.WriteAllText(
            Path.Combine(executionDirectory, $".manifest.json.{new string('1', 32)}.tmp"),
            "partial");

        JournalLoadResult result = await store.LoadDetailedAsync(manifest.Fingerprint, token);

        Assert.NotNull(result.Snapshot);
        JournalLoadProblem problem = Assert.Single(result.Problems);
        Assert.Equal(JournalLoadProblemKind.TemporaryFilePresent, problem.Kind);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task MissingAndMalformedManifestRemainStructuredAndAreNotRepaired()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        string directory = Path.Combine(root, manifest.Fingerprint.Value);
        Directory.CreateDirectory(directory);
        CancellationToken token = TestContext.Current.CancellationToken;

        JournalLoadResult missing = await store.LoadDetailedAsync(manifest.Fingerprint, token);
        Assert.Equal(JournalLoadProblemKind.MissingManifest, Assert.Single(missing.Problems).Kind);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), "not-json");
        JournalLoadResult malformed = await store.LoadDetailedAsync(manifest.Fingerprint, token);
        Assert.Equal(JournalLoadProblemKind.MalformedManifest, Assert.Single(malformed.Problems).Kind);
        Assert.Equal("not-json", File.ReadAllText(Path.Combine(directory, "manifest.json")));
    }

    [Fact]
    public async Task AlteredPayloadAndSequenceFilenameMismatchAreDetectedExactly()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.BeginAsync(manifest, Start(manifest), token);
        await store.PublishAsync(Intent(manifest, StartedAt.AddSeconds(1)), token);
        string directory = Path.Combine(root, manifest.Fingerprint.Value);
        string record = Path.Combine(directory, "00000000000000000001.record.json");
        string json = File.ReadAllText(record);
        File.WriteAllText(record, json.Replace("move-intent", "move-applied", StringComparison.Ordinal));

        JournalLoadResult altered = await store.LoadDetailedAsync(manifest.Fingerprint, token);
        Assert.Equal(JournalLoadProblemKind.PayloadHashMismatch, Assert.Single(altered.Problems).Kind);

        StoredJournalRecord stored = JournalStorageCodec.DecodeRecord(System.Text.Encoding.UTF8.GetBytes(json));
        File.WriteAllText(
            record,
            json.Replace(stored.PayloadHash.Value, new string('b', 64), StringComparison.Ordinal));
        JournalLoadResult alteredHash = await store.LoadDetailedAsync(manifest.Fingerprint, token);
        Assert.Equal(
            JournalLoadProblemKind.PayloadHashMismatch,
            Assert.Single(alteredHash.Problems).Kind);

        File.WriteAllText(record, json);
        File.Move(record, Path.Combine(directory, "00000000000000000002.record.json"));
        JournalLoadResult mismatch = await store.LoadDetailedAsync(manifest.Fingerprint, token);
        Assert.Equal(JournalLoadProblemKind.RecordFileNameMismatch, Assert.Single(mismatch.Problems).Kind);

        string renamed = Path.Combine(directory, "00000000000000000002.record.json");
        File.Move(renamed, record);
        File.WriteAllText(record, "not-json");
        JournalLoadResult malformed = await store.LoadDetailedAsync(manifest.Fingerprint, token);
        Assert.Equal(JournalLoadProblemKind.MalformedRecord, Assert.Single(malformed.Problems).Kind);
    }

    [Fact]
    public async Task UnsupportedVersionWrongFingerprintAndUnexpectedEntryAreStructured()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.BeginAsync(manifest, Start(manifest), token);
        string directory = Path.Combine(root, manifest.Fingerprint.Value);
        string manifestPath = Path.Combine(directory, "manifest.json");
        string original = File.ReadAllText(manifestPath);

        File.WriteAllText(
            manifestPath,
            original.Replace(
                "\"journalFormatVersion\": 1",
                "\"journalFormatVersion\": 99",
                StringComparison.Ordinal));
        Assert.Equal(
            JournalLoadProblemKind.UnsupportedJournalVersion,
            Assert.Single((await store.LoadDetailedAsync(manifest.Fingerprint, token)).Problems).Kind);

        File.WriteAllText(manifestPath, original);
        File.WriteAllText(
            manifestPath,
            original.Replace(manifest.Fingerprint.Value, new string('f', 64), StringComparison.Ordinal));
        Assert.Equal(
            JournalLoadProblemKind.WrongFingerprint,
            Assert.Single((await store.LoadDetailedAsync(manifest.Fingerprint, token)).Problems).Kind);

        File.WriteAllText(manifestPath, original);
        File.WriteAllText(Path.Combine(directory, "unexpected.bin"), "not a journal record");
        Assert.Equal(
            JournalLoadProblemKind.UnexpectedDurableEntry,
            Assert.Single((await store.LoadDetailedAsync(manifest.Fingerprint, token)).Problems).Kind);
    }

    [Fact]
    public async Task WrongPreviousHashRunAndFingerprintRemainDistinct()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.BeginAsync(manifest, Start(manifest), token);
        await store.PublishAsync(Intent(manifest, StartedAt.AddSeconds(1)), token);
        string recordPath = Path.Combine(
            root,
            manifest.Fingerprint.Value,
            "00000000000000000002.record.json");
        StoredJournalRecord previous = JournalStorageCodec.DecodeRecord(
            File.ReadAllBytes(Path.Combine(
                root,
                manifest.Fingerprint.Value,
                "00000000000000000001.record.json")));

        WriteRecord(
            recordPath,
            RecordPayload(
                manifest.Fingerprint.Value,
                RunId.Value,
                2,
                new JournalHash(new string('a', 64))));
        Assert.Equal(
            JournalLoadProblemKind.PreviousHashMismatch,
            Assert.Single((await store.LoadDetailedAsync(manifest.Fingerprint, token)).Problems).Kind);

        WriteRecord(
            recordPath,
            RecordPayload(
                manifest.Fingerprint.Value,
                new string('2', ExecutionRunId.HexLength),
                2,
                previous.PayloadHash));
        Assert.Equal(
            JournalLoadProblemKind.WrongRunOwnership,
            Assert.Single((await store.LoadDetailedAsync(manifest.Fingerprint, token)).Problems).Kind);

        WriteRecord(
            recordPath,
            RecordPayload(new string('f', 64), RunId.Value, 2, previous.PayloadHash));
        Assert.Equal(
            JournalLoadProblemKind.WrongFingerprint,
            Assert.Single((await store.LoadDetailedAsync(manifest.Fingerprint, token)).Problems).Kind);
    }

    [Fact]
    public async Task DuplicateEmbeddedSequenceGapAndRecordAfterTerminalAreStructured()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.BeginAsync(manifest, Start(manifest), token);
        ExecutionJournalRecord intent = Intent(manifest, StartedAt.AddSeconds(1));
        await store.PublishAsync(intent, token);
        string directory = Path.Combine(root, manifest.Fingerprint.Value);
        string firstPath = Path.Combine(directory, "00000000000000000001.record.json");
        string secondPath = Path.Combine(directory, "00000000000000000002.record.json");
        StoredJournalRecord first = JournalStorageCodec.DecodeRecord(File.ReadAllBytes(firstPath));
        File.WriteAllBytes(secondPath, JournalStorageCodec.EncodeRecord(first));
        Assert.Equal(
            JournalLoadProblemKind.DuplicateSequence,
            Assert.Single((await store.LoadDetailedAsync(manifest.Fingerprint, token)).Problems).Kind);

        File.Delete(firstPath);
        WriteRecord(
            secondPath,
            RecordPayload(
                manifest.Fingerprint.Value,
                RunId.Value,
                2,
                first.PayloadHash));
        Assert.Equal(
            JournalLoadProblemKind.SequenceGap,
            Assert.Single((await store.LoadDetailedAsync(manifest.Fingerprint, token)).Problems).Kind);

        File.Delete(secondPath);
        ExecutionJournalRecord terminal = ExecutionJournalRecord.ExecutionStopped(
            RunId,
            manifest.Fingerprint,
            1,
            StartedAt.AddSeconds(1),
            0,
            JournalRecordReasonKind.SourceRevalidationFailed);
        StoredJournalGenesis genesis = ReadGenesis(directory);
        StoredJournalRecord storedTerminal = JournalCanonicalHash.CreateRecord(
            StoredJournalRecordPayload.From(terminal, genesis.StartRecord.PayloadHash));
        File.WriteAllBytes(firstPath, JournalStorageCodec.EncodeRecord(storedTerminal));
        ExecutionJournalRecord afterTerminal = ExecutionJournalRecord.ExecutionCompleted(
            RunId,
            manifest.Fingerprint,
            2,
            StartedAt.AddSeconds(2));
        StoredJournalRecord storedAfter = JournalCanonicalHash.CreateRecord(
            StoredJournalRecordPayload.From(afterTerminal, storedTerminal.PayloadHash));
        File.WriteAllBytes(secondPath, JournalStorageCodec.EncodeRecord(storedAfter));
        Assert.Equal(
            JournalLoadProblemKind.RecordAfterTerminal,
            Assert.Single((await store.LoadDetailedAsync(manifest.Fingerprint, token)).Problems).Kind);
    }

    [Fact]
    public async Task RootStagingDirectoryIsReportedAndReturnedCollectionsStayImmutable()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.BeginAsync(manifest, Start(manifest), token);
        Directory.CreateDirectory(
            Path.Combine(root, $".{manifest.Fingerprint.Value}.{new string('1', 32)}.staging"));

        JournalLoadResult first = await store.LoadDetailedAsync(manifest.Fingerprint, token);
        JournalLoadResult second = await store.LoadDetailedAsync(manifest.Fingerprint, token);

        Assert.Equal(JournalLoadProblemKind.TemporaryFilePresent, Assert.Single(first.Problems).Kind);
        Assert.Equal(first.Snapshot!.Records, second.Snapshot!.Records);
        Assert.Throws<NotSupportedException>(
            () => ((IList<JournalLoadProblem>)first.Problems).Clear());
    }

    [Fact]
    public async Task RootStagingDiagnosticDoesNotTurnAbsentFingerprintIntoInvalidJournal()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        Directory.CreateDirectory(
            Path.Combine(root, $".{new string('a', 64)}.{new string('1', 32)}.staging"));
        ExecutionManifest manifest = JournalStoreTestManifest.Create();

        JournalLoadResult load = await store.LoadDetailedAsync(
            manifest.Fingerprint,
            TestContext.Current.CancellationToken);

        Assert.Equal(JournalLoadStatus.NotFound, load.Status);
        Assert.Null(load.Snapshot);
        Assert.Equal(JournalLoadProblemKind.TemporaryFilePresent, Assert.Single(load.Problems).Kind);
    }

    [Fact]
    public async Task DirectoryAtFixedRecordOrManifestNameIsStructuredUnsafeInput()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.BeginAsync(manifest, Start(manifest), token);
        string executionDirectory = Path.Combine(root, manifest.Fingerprint.Value);
        Directory.CreateDirectory(Path.Combine(executionDirectory, "00000000000000000001.record.json"));
        Assert.Equal(
            JournalLoadProblemKind.UnexpectedDurableEntry,
            Assert.Single((await store.LoadDetailedAsync(manifest.Fingerprint, token)).Problems).Kind);

        Directory.Delete(Path.Combine(executionDirectory, "00000000000000000001.record.json"));
        File.Delete(Path.Combine(executionDirectory, "manifest.json"));
        Directory.CreateDirectory(Path.Combine(executionDirectory, "manifest.json"));
        Assert.Equal(
            JournalLoadProblemKind.UnsafeJournalPath,
            Assert.Single((await store.LoadDetailedAsync(manifest.Fingerprint, token)).Problems).Kind);
    }

    [Fact]
    public async Task DirectoryMasqueradingAsTemporaryFileIsNotIgnored()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.BeginAsync(manifest, Start(manifest), token);
        Directory.CreateDirectory(
            Path.Combine(
                root,
                manifest.Fingerprint.Value,
                $".manifest.json.{new string('1', 32)}.tmp"));

        JournalLoadResult result = await store.LoadDetailedAsync(manifest.Fingerprint, token);

        Assert.Equal(JournalLoadStatus.Invalid, result.Status);
        Assert.Equal(JournalLoadProblemKind.UnexpectedDurableEntry, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public async Task UserMetadataCannotInfluenceExecutionDirectoryOrEscapeTheRoot()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create("metadata-token");
        await store.BeginAsync(
            manifest,
            Start(manifest),
            TestContext.Current.CancellationToken);

        string[] directories = Directory.GetDirectories(root);
        string only = Assert.Single(directories);
        Assert.Equal(manifest.Fingerprint.Value, Path.GetFileName(only));
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, only);
        Assert.DoesNotContain("metadata-token", only, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new PlanFingerprint("..\\escape"));
        Assert.Throws<ArgumentException>(() => new ExecutionRunId("..\\escape"));
    }

    [Fact]
    public void RejectsRelativeRoot()
    {
        Assert.Throws<ArgumentException>(() => new FileExecutionJournalStore("relative"));
        Assert.Throws<ArgumentException>(
            () => new FileExecutionJournalStore(Path.GetPathRoot(Path.GetTempPath())!));
    }

    [Fact]
    public void RejectsAReparseAncestorBeforeCreatingTheJournalRoot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This is a Windows path-safety test.");

        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string target = Path.Combine(temp.FullPath, "target");
        string link = Path.Combine(temp.FullPath, "link");
        Directory.CreateDirectory(target);

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip(
                $"Symbolic links are unavailable in this test environment: {exception.GetType().Name}");
        }

        string escapedRoot = Path.Combine(link, "journal");

        Assert.Throws<IOException>(() => new FileExecutionJournalStore(escapedRoot));
        Assert.False(Directory.Exists(Path.Combine(target, "journal")));
    }

    [Fact]
    public void DurablePublisherUsesNoReplaceAndLeavesNoTemporaryFile()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string directory = Path.Combine(temp.FullPath, "journal");
        Directory.CreateDirectory(directory);
        string final = Path.Combine(directory, "record.json");
        File.WriteAllText(final, "original");
        DurableJournalFilePublisher publisher = new();

        Assert.Throws<IOException>(
            () => publisher.PublishFile(directory, "record.json", "changed"u8));

        Assert.Equal("original", File.ReadAllText(final));
        Assert.Empty(Directory.GetFiles(directory, ".*.tmp"));
    }

    [Fact]
    public async Task FailedGenesisDirectoryPublicationCleansOnlyUnpublishedStaging()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root, new FailingDirectoryPublisher());
        ExecutionManifest manifest = JournalStoreTestManifest.Create();

        await Assert.ThrowsAsync<IOException>(
            () => store.BeginAsync(
                manifest,
                Start(manifest),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Empty(Directory.GetDirectories(root));
        Assert.False(Directory.Exists(Path.Combine(root, manifest.Fingerprint.Value)));
    }

    private static ExecutionJournalRecord Start(ExecutionManifest manifest) =>
        ExecutionJournalRecord.ExecutionStarted(RunId, manifest.Fingerprint, 0, StartedAt);

    private static ExecutionJournalRecord Intent(
        ExecutionManifest manifest,
        DateTimeOffset recordedAt) =>
        ExecutionJournalRecord.MoveIntent(
            RunId,
            manifest.Fingerprint,
            1,
            recordedAt,
            0,
            manifest.Items[0].SourcePrecondition.PhysicalIdentity!);

    private static StoredJournalRecordPayload RecordPayload(
        string fingerprint,
        string runId,
        long sequence,
        JournalHash previousHash) =>
        new(
            fingerprint,
            runId,
            sequence,
            previousHash,
            StartedAt.AddSeconds(sequence).Ticks,
            ExecutionJournalRecordKind.MoveNotApplied,
            0,
            expectedEntryIdentity: null,
            JournalRecordReasonKind.MoveDidNotApply);

    private static void WriteRecord(string path, StoredJournalRecordPayload payload) =>
        File.WriteAllBytes(
            path,
            JournalStorageCodec.EncodeRecord(JournalCanonicalHash.CreateRecord(payload)));

    private static StoredJournalGenesis ReadGenesis(string executionDirectory) =>
        JournalStorageCodec.DecodeGenesis(
            File.ReadAllBytes(Path.Combine(executionDirectory, "manifest.json")));

    private sealed class FailingDirectoryPublisher : IJournalFilePublisher
    {
        private readonly DurableJournalFilePublisher _inner = new();

        public void PublishFile(
            string directory,
            string finalFileName,
            ReadOnlySpan<byte> content) =>
            _inner.PublishFile(directory, finalFileName, content);

        public void PublishDirectory(string stagingDirectory, string finalDirectory) =>
            throw new IOException("Injected directory publication failure.");
    }
}
