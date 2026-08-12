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
        Assert.Equal(RunId, Assert.IsType<ExecutionJournalSnapshot>(result.Snapshot).RunId);
        Assert.Equal(before, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public async Task CorruptSameFingerprintReturnsStructuredInvalidBeginWithoutOverwrite()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.BeginAsync(manifest, Start(manifest), token);
        string manifestPath = Path.Combine(root, manifest.Fingerprint.Value, "manifest.json");
        File.WriteAllText(manifestPath, "not-json");

        JournalBeginResult result = await store.BeginAsync(manifest, Start(manifest), token);

        Assert.Equal(JournalBeginStatus.InvalidExistingJournal, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Equal(manifest.Fingerprint, result.ExistingFingerprint);
        Assert.Equal(
            ExecutionJournalReadProblemKind.ManifestMalformed,
            Assert.Single(result.Problems).Kind);
        Assert.Equal("not-json", File.ReadAllText(manifestPath));
    }

    [Fact]
    public async Task CorruptDifferentFingerprintBlocksBeginWithItsStructuredOwnership()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest blocker = JournalStoreTestManifest.Create("blocker");
        ExecutionManifest requested = JournalStoreTestManifest.Create("requested");
        Assert.NotEqual(blocker.Fingerprint, requested.Fingerprint);
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.BeginAsync(blocker, Start(blocker), token);
        string blockerManifest = Path.Combine(root, blocker.Fingerprint.Value, "manifest.json");
        File.WriteAllText(blockerManifest, "not-json");

        JournalBeginResult result = await store.BeginAsync(requested, Start(requested), token);

        Assert.Equal(JournalBeginStatus.InvalidExistingJournal, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Equal(blocker.Fingerprint, result.ExistingFingerprint);
        Assert.Equal(
            ExecutionJournalReadProblemKind.ManifestMalformed,
            Assert.Single(result.Problems).Kind);
        Assert.False(Directory.Exists(Path.Combine(root, requested.Fingerprint.Value)));
        Assert.Equal("not-json", File.ReadAllText(blockerManifest));
    }

    [Fact]
    public async Task MultipleUnresolvedOwnersBecomeCorruptionInsteadOfFirstOwnerWins()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        string secondRoot = Path.Combine(temp.FullPath, "second-journal");
        FileExecutionJournalStore firstStore = new(root);
        FileExecutionJournalStore secondStore = new(secondRoot);
        ExecutionManifest first = JournalStoreTestManifest.Create("first-owner");
        ExecutionManifest second = JournalStoreTestManifest.Create("second-owner");
        ExecutionManifest requested = JournalStoreTestManifest.Create("requested-owner");
        CancellationToken token = TestContext.Current.CancellationToken;
        await firstStore.BeginAsync(first, Start(first), token);
        await secondStore.BeginAsync(second, Start(second), token);
        Directory.Move(
            Path.Combine(secondRoot, second.Fingerprint.Value),
            Path.Combine(root, second.Fingerprint.Value));

        JournalBeginResult result = await firstStore.BeginAsync(
            requested,
            Start(requested),
            token);

        Assert.Equal(JournalBeginStatus.InvalidExistingJournal, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Null(result.ExistingFingerprint);
        Assert.Equal(
            ExecutionJournalReadProblemKind.ConflictingExecutionOwnership,
            Assert.Single(result.Problems).Kind);
        Assert.False(Directory.Exists(Path.Combine(root, requested.Fingerprint.Value)));
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
    public async Task ReplayReadDistinguishesAbsenceValidatedDataAndCorruption()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        CancellationToken token = TestContext.Current.CancellationToken;

        ExecutionJournalReadResult absent = await store.ReadForReplayAsync(
            manifest.Fingerprint,
            token);
        Assert.Equal(ExecutionJournalReadStatus.NotFound, absent.Status);
        Assert.Null(absent.Snapshot);
        Assert.Empty(absent.Problems);

        await store.BeginAsync(manifest, Start(manifest), token);
        string directory = Path.Combine(root, manifest.Fingerprint.Value);
        File.WriteAllText(
            Path.Combine(directory, $".manifest.json.{new string('1', 32)}.tmp"),
            "unpublished");
        ExecutionJournalReadResult loaded = await store.ReadForReplayAsync(
            manifest.Fingerprint,
            token);
        Assert.Equal(ExecutionJournalReadStatus.Loaded, loaded.Status);
        Assert.Single(Assert.IsType<ExecutionJournalSnapshot>(loaded.Snapshot).Records);
        Assert.Equal(
            ExecutionJournalReadProblemKind.TemporaryArtifactIgnored,
            Assert.Single(loaded.Problems).Kind);
        Assert.Throws<NotSupportedException>(
            () => ((IList<ExecutionJournalReadProblem>)loaded.Problems).Clear());

        File.WriteAllText(Path.Combine(directory, "manifest.json"), "not-json");
        ExecutionJournalReadResult invalid = await store.ReadForReplayAsync(
            manifest.Fingerprint,
            token);
        Assert.Equal(ExecutionJournalReadStatus.Invalid, invalid.Status);
        Assert.Null(invalid.Snapshot);
        ExecutionJournalReadProblem problem = Assert.Single(invalid.Problems);
        Assert.Equal(ExecutionJournalReadProblemKind.ManifestMalformed, problem.Kind);
        Assert.True(problem.RequiresManualIntervention);
    }

    [Theory]
    [MemberData(nameof(DurableProblemMappings))]
    public void EveryDurableValidationProblemHasAStableApplicationMapping(
        JournalLoadProblemKind durableKind,
        ExecutionJournalReadProblemKind expectedKind)
    {
        JournalLoadProblem durable = new(
            durableKind,
            "Adapter-specific detail must not cross the replay boundary.",
            RecordSequence: 7,
            FileName: "private.record.json");
        JournalLoadResult load = new(null, [durable]);

        ExecutionJournalReadResult mapped = FileExecutionJournalStore.MapForReplay(load);

        ExecutionJournalReadProblem problem = Assert.Single(mapped.Problems);
        Assert.Equal(expectedKind, problem.Kind);
        Assert.Equal(7, problem.RecordSequence);
        Assert.Equal(
            durableKind == JournalLoadProblemKind.TemporaryFilePresent
                ? ExecutionJournalReadStatus.NotFound
                : ExecutionJournalReadStatus.Invalid,
            mapped.Status);
        Assert.Null(mapped.Snapshot);
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
    public async Task TemporaryDiagnosticsAndUnexpectedEntrySelectionUseOrdinalFileNameOrder()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.BeginAsync(manifest, Start(manifest), token);
        string directory = Path.Combine(root, manifest.Fingerprint.Value);

        File.WriteAllText(
            Path.Combine(directory, $".manifest.json.{new string('f', 32)}.tmp"),
            "later");
        File.WriteAllText(
            Path.Combine(directory, $".manifest.json.{new string('1', 32)}.tmp"),
            "earlier");

        JournalLoadResult temporary = await store.LoadDetailedAsync(manifest.Fingerprint, token);
        Assert.Equal(
            [
                $".manifest.json.{new string('1', 32)}.tmp",
                $".manifest.json.{new string('f', 32)}.tmp",
            ],
            temporary.Problems.Select(static problem => problem.FileName));

        File.WriteAllText(Path.Combine(directory, "z-unexpected"), "later");
        File.WriteAllText(Path.Combine(directory, "a-unexpected"), "earlier");

        JournalLoadProblem unexpected = Assert.Single(
            (await store.LoadDetailedAsync(manifest.Fingerprint, token)).Problems);
        Assert.Equal(JournalLoadProblemKind.UnexpectedDurableEntry, unexpected.Kind);
        Assert.Equal("a-unexpected", unexpected.FileName);
    }

    [Fact]
    public async Task DurableRecordCreationOrderCannotChangeValidatedReplayOrder()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string root = Path.Combine(temp.FullPath, "journal");
        FileExecutionJournalStore store = new(root);
        ExecutionManifest manifest = JournalStoreTestManifest.Create();
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.BeginAsync(manifest, Start(manifest), token);
        string directory = Path.Combine(root, manifest.Fingerprint.Value);
        StoredJournalGenesis genesis = ReadGenesis(directory);
        ExecutionJournalRecord intent = Intent(manifest, StartedAt.AddSeconds(1));
        StoredJournalRecord storedIntent = JournalCanonicalHash.CreateRecord(
            StoredJournalRecordPayload.From(intent, genesis.StartRecord.PayloadHash));
        ExecutionJournalRecord notApplied = ExecutionJournalRecord.MoveNotApplied(
            RunId,
            manifest.Fingerprint,
            2,
            StartedAt.AddSeconds(2),
            0,
            JournalRecordReasonKind.MoveDidNotApply);
        StoredJournalRecord storedNotApplied = JournalCanonicalHash.CreateRecord(
            StoredJournalRecordPayload.From(notApplied, storedIntent.PayloadHash));

        File.WriteAllBytes(
            Path.Combine(directory, "00000000000000000002.record.json"),
            JournalStorageCodec.EncodeRecord(storedNotApplied));
        File.WriteAllBytes(
            Path.Combine(directory, "00000000000000000001.record.json"),
            JournalStorageCodec.EncodeRecord(storedIntent));

        JournalLoadResult first = await store.LoadDetailedAsync(manifest.Fingerprint, token);
        JournalLoadResult second = await store.LoadDetailedAsync(manifest.Fingerprint, token);

        Assert.Equal(JournalLoadStatus.Loaded, first.Status);
        Assert.Equal([0L, 1L, 2L], first.Snapshot!.Records.Select(static record => record.Sequence));
        Assert.Equal(first.Snapshot.Records, second.Snapshot!.Records);
        Assert.Equal(
            ExecutionJournalStatus.Resumable,
            ExecutionJournalStateMachine.Replay(first.Snapshot).Status);
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

    public static TheoryData<JournalLoadProblemKind, ExecutionJournalReadProblemKind>
        DurableProblemMappings => new()
        {
            { JournalLoadProblemKind.MissingManifest, ExecutionJournalReadProblemKind.ManifestMissing },
            { JournalLoadProblemKind.MalformedManifest, ExecutionJournalReadProblemKind.ManifestMalformed },
            { JournalLoadProblemKind.UnsupportedJournalVersion, ExecutionJournalReadProblemKind.UnsupportedFormat },
            { JournalLoadProblemKind.WrongFingerprint, ExecutionJournalReadProblemKind.FingerprintOwnershipMismatch },
            { JournalLoadProblemKind.MalformedRecord, ExecutionJournalReadProblemKind.RecordMalformed },
            { JournalLoadProblemKind.DuplicateSequence, ExecutionJournalReadProblemKind.SequenceViolation },
            { JournalLoadProblemKind.SequenceGap, ExecutionJournalReadProblemKind.SequenceViolation },
            { JournalLoadProblemKind.RecordFileNameMismatch, ExecutionJournalReadProblemKind.SequenceViolation },
            { JournalLoadProblemKind.PayloadHashMismatch, ExecutionJournalReadProblemKind.PayloadIntegrityViolation },
            { JournalLoadProblemKind.PreviousHashMismatch, ExecutionJournalReadProblemKind.HashChainViolation },
            { JournalLoadProblemKind.WrongRunOwnership, ExecutionJournalReadProblemKind.RunOwnershipMismatch },
            { JournalLoadProblemKind.RecordAfterTerminal, ExecutionJournalReadProblemKind.TerminalTail },
            { JournalLoadProblemKind.TemporaryFilePresent, ExecutionJournalReadProblemKind.TemporaryArtifactIgnored },
            { JournalLoadProblemKind.UnexpectedDurableEntry, ExecutionJournalReadProblemKind.UnexpectedStorageEntry },
            { JournalLoadProblemKind.UnsafeJournalPath, ExecutionJournalReadProblemKind.UnsafeStorage },
            { JournalLoadProblemKind.IoFailure, ExecutionJournalReadProblemKind.StorageReadFailure },
        };

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
