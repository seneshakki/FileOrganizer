using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;

namespace FileOrganizer.Infrastructure.Journaling;

/// <summary>
/// Stores immutable execution journals beneath one caller-selected application-data root.
/// </summary>
/// <remarks>
/// Publication uses same-directory temporary files, durable file flush, and no-replace
/// renames. .NET does not expose a portable directory flush, so this does not claim that
/// a newly published directory entry survives every sudden-power-loss scenario.
/// This type serializes callers in this process; cross-process exclusion is deferred.
/// </remarks>
public sealed class FileExecutionJournalStore : IExecutionJournalStore
{
    private const string ManifestFileName = "manifest.json";
    private const string RecordSuffix = ".record.json";
    private const int SequenceDigits = 20;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RootGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static StringComparer HostPathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly StringComparison _pathComparison;
    private readonly IJournalFilePublisher _publisher;
    private readonly SemaphoreSlim _gate;

    public FileExecutionJournalStore(string journalRoot)
        : this(journalRoot, new DurableJournalFilePublisher())
    {
    }

    internal FileExecutionJournalStore(string journalRoot, IJournalFilePublisher publisher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalRoot);
        ArgumentNullException.ThrowIfNull(publisher);
        if (!Path.IsPathFullyQualified(journalRoot))
        {
            throw new ArgumentException("The journal root must be fully qualified.", nameof(journalRoot));
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(journalRoot));
        string volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(_root)!);
        if (HostPathComparer.Equals(volumeRoot, _root))
        {
            throw new ArgumentException(
                "A volume root is too broad to use as a journal root.",
                nameof(journalRoot));
        }

        _rootPrefix = _root + Path.DirectorySeparatorChar;
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _publisher = publisher;
        _gate = RootGates.GetOrAdd(
            OperatingSystem.IsWindows() ? _root.ToUpperInvariant() : _root,
            static _ => new SemaphoreSlim(1, 1));
        EnsureRootIsSafe(createWhenMissing: false);
    }

    public async ValueTask<ExecutionJournalSnapshot?> ReadAsync(
        PlanFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        JournalLoadResult result = await LoadDetailedAsync(fingerprint, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == JournalLoadStatus.NotFound)
        {
            return null;
        }

        if (!result.Succeeded)
        {
            throw new JournalStoreException(result);
        }

        return result.Snapshot;
    }

    /// <summary>
    /// Reads and validates one durable journal for Application-owned replay.
    /// Corrupt persistence never crosses this boundary as a replayable snapshot.
    /// </summary>
    public async ValueTask<ExecutionJournalReadResult> ReadForReplayAsync(
        PlanFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        JournalLoadResult result = await LoadDetailedAsync(fingerprint, cancellationToken)
            .ConfigureAwait(false);
        return MapForReplay(result);
    }

    public async ValueTask<JournalLoadResult> LoadDetailedAsync(
        PlanFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return LoadDetailedUnderGate(fingerprint, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<JournalBeginResult> BeginAsync(
        ExecutionManifest manifest,
        ExecutionJournalRecord startRecord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(startRecord);
        ValidateStart(manifest, startRecord);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureRootIsSafe(createWhenMissing: true);
            JournalLoadResult same = LoadDetailedUnderGate(manifest.Fingerprint, cancellationToken);
            if (same.Status != JournalLoadStatus.NotFound)
            {
                if (!same.Succeeded)
                {
                    return InvalidBegin(manifest.Fingerprint, same);
                }
            }

            List<(PlanFingerprint Fingerprint, ExecutionJournalSnapshot Snapshot)> unresolved = [];
            foreach (string executionDirectory in EnumerateExecutionDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(executionDirectory);
                if (!TryFingerprint(name, out PlanFingerprint? otherFingerprint))
                {
                    return JournalBeginResult.ConflictingExecutionOwnership(
                    [
                        new ExecutionJournalReadProblem(
                            ExecutionJournalReadProblemKind.UnexpectedStorageEntry),
                    ]);
                }

                JournalLoadResult other = otherFingerprint!.Equals(manifest.Fingerprint)
                    ? same
                    : LoadDetailedUnderGate(otherFingerprint, cancellationToken);
                if (!other.Succeeded)
                {
                    return InvalidBegin(otherFingerprint, other);
                }

                ExecutionJournalState state = ExecutionJournalStateMachine.Replay(other.Snapshot!);
                if (!state.ReleasesGlobalExecutionOwnership)
                {
                    unresolved.Add((otherFingerprint, other.Snapshot!));
                }
            }

            if (unresolved.Count > 1)
            {
                return JournalBeginResult.ConflictingExecutionOwnership(
                [
                    new ExecutionJournalReadProblem(
                        ExecutionJournalReadProblemKind.ConflictingExecutionOwnership),
                ]);
            }

            if (same.Status == JournalLoadStatus.Loaded)
            {
                return new JournalBeginResult(JournalBeginStatus.AlreadyExists, same.Snapshot!);
            }

            if (unresolved.Count == 1)
            {
                return new JournalBeginResult(
                    JournalBeginStatus.BlockedByUnresolvedExecution,
                    unresolved[0].Snapshot);
            }

            string staging = ContainedPath($".{manifest.Fingerprint.Value}.{Guid.NewGuid():N}.staging");
            string finalDirectory = ExecutionDirectory(manifest.Fingerprint);
            StoredJournalGenesis requestedGenesis = CreateGenesis(manifest, startRecord);
            bool published = false;
            try
            {
                try
                {
                    Directory.CreateDirectory(staging);
                    _publisher.PublishFile(
                        staging,
                        ManifestFileName,
                        JournalStorageCodec.EncodeGenesis(requestedGenesis));
                    cancellationToken.ThrowIfCancellationRequested();
                    _publisher.PublishDirectory(staging, finalDirectory);
                    published = true;
                    return new JournalBeginResult(
                        JournalBeginStatus.Begun,
                        requestedGenesis.ToSnapshot());
                }
                catch (Exception exception) when (IsExpectedPersistenceFailure(exception))
                {
                    JournalLoadResult afterFailure = LoadDetailedUnderGate(
                        manifest.Fingerprint,
                        CancellationToken.None);
                    if (afterFailure.Status == JournalLoadStatus.Loaded)
                    {
                        return new JournalBeginResult(
                            JournalBeginStatus.AlreadyExists,
                            afterFailure.Snapshot!);
                    }

                    return afterFailure.Status == JournalLoadStatus.Invalid
                        ? InvalidBegin(manifest.Fingerprint, afterFailure)
                        : JournalBeginResult.PersistenceFailure();
                }
            }
            finally
            {
                if (!published)
                {
                    TryDeleteStagingDirectory(staging);
                }
            }
        }
        catch (Exception exception) when (
            exception is not JournalStoreException
            && IsExpectedPersistenceFailure(exception))
        {
            return JournalBeginResult.PersistenceFailure();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<JournalPublishResult> PublishAsync(
        ExecutionJournalRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JournalLoadResult load = LoadDetailedUnderGate(record.Fingerprint, cancellationToken);
            if (!load.Succeeded)
            {
                return JournalPublishResult.InvalidJournal();
            }

            ExecutionJournalSnapshot snapshot = load.Snapshot!;
            if (!snapshot.RunId.Equals(record.RunId))
            {
                return JournalPublishResult.SequenceConflict();
            }

            if (record.Sequence < snapshot.Records.Count)
            {
                ExecutionJournalRecord existing = snapshot.Records[checked((int)record.Sequence)];
                return existing.Equals(record)
                    ? JournalPublishResult.AlreadyPublished(existing)
                    : JournalPublishResult.SequenceConflict(existing);
            }

            if (record.Sequence != snapshot.Records.Count)
            {
                return JournalPublishResult.SequenceConflict();
            }

            _ = ExecutionJournalStateMachine.Replay(
                new ExecutionJournalSnapshot(
                    snapshot.RunId,
                    snapshot.Manifest,
                    snapshot.Records.Append(record)));

            StoredJournalRecord previous;
            try
            {
                previous = ReadStoredRecordForSequence(
                    record.Fingerprint,
                    record.Sequence - 1,
                    cancellationToken);
            }
            catch (Exception exception) when (IsInvalidStoredRecordFailure(exception))
            {
                return JournalPublishResult.InvalidJournal();
            }

            StoredJournalRecord stored = JournalCanonicalHash.CreateRecord(
                StoredJournalRecordPayload.From(record, previous.PayloadHash));
            string directory = ExecutionDirectory(record.Fingerprint);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _publisher.PublishFile(
                    directory,
                    RecordFileName(record.Sequence),
                    JournalStorageCodec.EncodeRecord(stored));
            }
            catch (Exception exception) when (IsExpectedPersistenceFailure(exception))
            {
                JournalLoadResult raced = LoadDetailedUnderGate(
                    record.Fingerprint,
                    CancellationToken.None);
                if (!raced.Succeeded)
                {
                    return JournalPublishResult.InvalidJournal();
                }

                if (record.Sequence < raced.Snapshot!.Records.Count)
                {
                    ExecutionJournalRecord existing =
                        raced.Snapshot.Records[checked((int)record.Sequence)];
                    return existing.Equals(record)
                        ? JournalPublishResult.AlreadyPublished(existing)
                        : JournalPublishResult.SequenceConflict(existing);
                }

                return JournalPublishResult.PersistenceFailure();
            }

            return JournalPublishResult.Published();
        }
        finally
        {
            _gate.Release();
        }
    }

    private JournalLoadResult LoadDetailedUnderGate(
        PlanFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureRootIsSafe(createWhenMissing: false);
            JournalLoadProblem? unexpectedRoot = FindUnexpectedRootEntry();
            if (unexpectedRoot is not null)
            {
                return new JournalLoadResult(null, [unexpectedRoot]);
            }

            string directory = ExecutionDirectory(fingerprint);
            if (File.Exists(directory))
            {
                return Failure(
                    JournalLoadProblemKind.UnsafeJournalPath,
                    "The fixed execution-directory path is occupied by a file.");
            }

            if (!Directory.Exists(directory))
            {
                return new JournalLoadResult(null, FindRootStagingDirectories());
            }

            if (IsReparsePoint(directory))
            {
                return Failure(JournalLoadProblemKind.UnsafeJournalPath, "An execution directory cannot be a reparse point.");
            }

            List<JournalLoadProblem> warnings = FindRootStagingDirectories();
            warnings.AddRange(FindTemporaryFiles(directory));
            JournalLoadProblem? unexpected = FindUnexpectedEntry(directory);
            if (unexpected is not null)
            {
                return new JournalLoadResult(null, [unexpected]);
            }

            string manifestPath = Path.Combine(directory, ManifestFileName);
            if (Directory.Exists(manifestPath))
            {
                return Failure(
                    JournalLoadProblemKind.UnsafeJournalPath,
                    "The fixed manifest-file path is occupied by a directory.");
            }

            if (!File.Exists(manifestPath))
            {
                return Failure(JournalLoadProblemKind.MissingManifest, "The durable manifest is missing.");
            }

            if (IsReparsePoint(manifestPath))
            {
                return Failure(JournalLoadProblemKind.UnsafeJournalPath, "The durable manifest cannot be a reparse point.");
            }

            StoredJournalGenesis genesis;
            try
            {
                genesis = JournalStorageCodec.DecodeGenesis(File.ReadAllBytes(manifestPath));
            }
            catch (NotSupportedException exception)
            {
                return Failure(JournalLoadProblemKind.UnsupportedJournalVersion, exception.Message);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
            {
                return Failure(JournalLoadProblemKind.MalformedManifest, exception.Message);
            }

            if (genesis.JournalFormatVersion != JournalCanonicalHash.JournalFormatVersion
                || genesis.CanonicalHashVersion != JournalCanonicalHash.CanonicalHashVersion)
            {
                return Failure(JournalLoadProblemKind.UnsupportedJournalVersion, "The durable journal format or canonical hash version is unsupported.");
            }

            if (!StringComparer.Ordinal.Equals(genesis.Manifest.Fingerprint, fingerprint.Value))
            {
                return Failure(JournalLoadProblemKind.WrongFingerprint, "The manifest belongs to a different plan fingerprint.");
            }

            if (!JournalCanonicalHash.HasValidPayloadHash(genesis.StartRecord)
                || !JournalCanonicalHash.HasValidManifestHash(genesis))
            {
                return Failure(JournalLoadProblemKind.PayloadHashMismatch, "The manifest or genesis payload hash does not match its canonical content.");
            }

            ExecutionJournalSnapshot genesisSnapshot;
            try
            {
                genesisSnapshot = genesis.ToSnapshot();
            }
            catch (NotSupportedException exception)
            {
                return Failure(JournalLoadProblemKind.UnsupportedJournalVersion, exception.Message);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Failure(JournalLoadProblemKind.MalformedManifest, exception.Message);
            }

            List<ExecutionJournalRecord> records = [genesisSnapshot.Records[0]];
            JournalHash previousHash = genesis.StartRecord.PayloadHash;
            string? malformedRecordName = Directory.EnumerateFiles(
                    directory,
                    "*" + RecordSuffix,
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .FirstOrDefault(static name => !TryParseRecordFileName(name!, out _));
            if (malformedRecordName is not null)
            {
                return Failure(
                    JournalLoadProblemKind.MalformedRecord,
                    "A durable record has an invalid fixed file name.",
                    fileName: malformedRecordName);
            }

            string[] recordFiles = Directory.EnumerateFiles(directory, "*" + RecordSuffix, SearchOption.TopDirectoryOnly)
                .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();
            HashSet<long> sequences = [];
            HashSet<long> embeddedSequences = [];
            foreach (string path in recordFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(path);
                if (IsReparsePoint(path))
                {
                    return Failure(JournalLoadProblemKind.UnsafeJournalPath, "A durable record cannot be a reparse point.", fileName: fileName);
                }

                if (!TryParseRecordFileName(fileName, out long sequence))
                {
                    return Failure(JournalLoadProblemKind.MalformedRecord, "A durable record has an invalid fixed file name.", fileName: fileName);
                }

                if (!sequences.Add(sequence))
                {
                    return Failure(JournalLoadProblemKind.DuplicateSequence, "Two durable record names identify the same sequence.", sequence, fileName);
                }

                StoredJournalRecord stored;
                try
                {
                    stored = JournalStorageCodec.DecodeRecord(File.ReadAllBytes(path));
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
                {
                    return Failure(JournalLoadProblemKind.MalformedRecord, exception.Message, sequence, fileName);
                }

                if (stored.JournalFormatVersion != JournalCanonicalHash.JournalFormatVersion
                    || stored.CanonicalHashVersion != JournalCanonicalHash.CanonicalHashVersion)
                {
                    return Failure(JournalLoadProblemKind.UnsupportedJournalVersion, "A durable record version is unsupported.", sequence, fileName);
                }

                if (!embeddedSequences.Add(stored.Payload.Sequence))
                {
                    return Failure(JournalLoadProblemKind.DuplicateSequence, "Two durable records embed the same sequence.", stored.Payload.Sequence, fileName);
                }

                if (stored.Payload.Sequence != sequence)
                {
                    return Failure(JournalLoadProblemKind.RecordFileNameMismatch, "The durable record filename does not match its embedded sequence.", sequence, fileName);
                }

                if (sequence != records.Count)
                {
                    return Failure(JournalLoadProblemKind.SequenceGap, "The durable record sequence is not contiguous.", sequence, fileName);
                }

                if (!JournalCanonicalHash.HasValidPayloadHash(stored))
                {
                    return Failure(JournalLoadProblemKind.PayloadHashMismatch, "A durable record payload hash is invalid.", sequence, fileName);
                }

                if (!Equals(stored.Payload.PreviousRecordHash, previousHash))
                {
                    return Failure(JournalLoadProblemKind.PreviousHashMismatch, "A durable record does not chain to its predecessor.", sequence, fileName);
                }

                if (!StringComparer.Ordinal.Equals(stored.Payload.Fingerprint, fingerprint.Value))
                {
                    return Failure(JournalLoadProblemKind.WrongFingerprint, "A durable record belongs to a different plan.", sequence, fileName);
                }

                if (!StringComparer.Ordinal.Equals(stored.Payload.RunId, genesis.RunId))
                {
                    return Failure(JournalLoadProblemKind.WrongRunOwnership, "A durable record belongs to a different execution run.", sequence, fileName);
                }

                try
                {
                    records.Add(stored.ToDomain());
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    return Failure(JournalLoadProblemKind.MalformedRecord, exception.Message, sequence, fileName);
                }

                previousHash = stored.PayloadHash;
            }

            ExecutionJournalSnapshot snapshot = new(
                genesisSnapshot.RunId,
                genesisSnapshot.Manifest,
                records);
            try
            {
                _ = ExecutionJournalStateMachine.Replay(snapshot);
            }
            catch (InvalidJournalSequenceException exception)
            {
                JournalLoadProblemKind kind = exception.ProblemKind
                    == JournalSequenceProblemKind.RecordAfterTerminal
                        ? JournalLoadProblemKind.RecordAfterTerminal
                        : JournalLoadProblemKind.MalformedRecord;
                return Failure(kind, exception.Message, exception.RecordSequence);
            }

            return new JournalLoadResult(snapshot, warnings);
        }
        catch (JournalStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(JournalLoadProblemKind.IoFailure, exception.Message);
        }
    }

    private StoredJournalRecord ReadStoredRecordForSequence(
        PlanFingerprint fingerprint,
        long sequence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sequence == 0)
        {
            string manifest = Path.Combine(ExecutionDirectory(fingerprint), ManifestFileName);
            return JournalStorageCodec.DecodeGenesis(File.ReadAllBytes(manifest)).StartRecord;
        }

        string path = Path.Combine(ExecutionDirectory(fingerprint), RecordFileName(sequence));
        return JournalStorageCodec.DecodeRecord(File.ReadAllBytes(path));
    }

    private static StoredJournalGenesis CreateGenesis(
        ExecutionManifest manifest,
        ExecutionJournalRecord startRecord)
    {
        StoredJournalRecord storedStart = JournalCanonicalHash.CreateRecord(
            StoredJournalRecordPayload.From(startRecord, previousRecordHash: null));
        return JournalCanonicalHash.CreateGenesis(
            startRecord.RunId.Value,
            StoredExecutionManifest.From(manifest),
            storedStart);
    }

    private IEnumerable<string> EnumerateExecutionDirectories() =>
        Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly)
            .Where(static path => !Path.GetFileName(path).StartsWith('.'))
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal);

    private string ExecutionDirectory(PlanFingerprint fingerprint) =>
        ContainedPath(fingerprint.Value);

    private string ContainedPath(string singleSegment)
    {
        if (singleSegment.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("A journal path component must be one validated segment.", nameof(singleSegment));
        }

        string fullPath = Path.GetFullPath(Path.Combine(_root, singleSegment));
        if (!fullPath.StartsWith(_rootPrefix, _pathComparison))
        {
            throw new InvalidOperationException("A journal path escaped its configured root.");
        }

        return fullPath;
    }

    private void EnsureRootIsSafe(bool createWhenMissing)
    {
        RejectReparsePointsInExistingAncestors(_root);

        if (!Directory.Exists(_root))
        {
            if (!createWhenMissing)
            {
                return;
            }

            Directory.CreateDirectory(_root);
            RejectReparsePointsInExistingAncestors(_root);
        }
    }

    private static void RejectReparsePointsInExistingAncestors(string path)
    {
        string? current = path;
        while (current is not null)
        {
            if (Directory.Exists(current) && IsReparsePoint(current))
            {
                throw new IOException("The journal root or one of its existing ancestors is a reparse point.");
            }

            current = Directory.GetParent(current)?.FullName;
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static List<JournalLoadProblem> FindTemporaryFiles(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, ".*.tmp", SearchOption.TopDirectoryOnly)
            .Where(static path =>
                File.Exists(path)
                && !IsReparsePoint(path)
                && IsValidTemporaryFileName(Path.GetFileName(path)))
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(path => new JournalLoadProblem(
                JournalLoadProblemKind.TemporaryFilePresent,
                "An unpublished temporary journal file was ignored.",
                FileName: Path.GetFileName(path)))
            .ToList();

    private static JournalLoadProblem? FindUnexpectedEntry(string directory)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(
                     directory,
                     "*",
                     SearchOption.TopDirectoryOnly)
                 .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            string fileName = Path.GetFileName(path);
            bool recognized = StringComparer.Ordinal.Equals(fileName, ManifestFileName)
                || (File.Exists(path) && fileName.EndsWith(RecordSuffix, StringComparison.Ordinal))
                || (File.Exists(path)
                    && !IsReparsePoint(path)
                    && IsValidTemporaryFileName(fileName));
            if (!recognized)
            {
                return new JournalLoadProblem(
                    JournalLoadProblemKind.UnexpectedDurableEntry,
                    "The execution directory contains an unrecognized durable entry.",
                    FileName: fileName);
            }
        }

        return null;
    }

    private List<JournalLoadProblem> FindRootStagingDirectories() =>
        Directory.Exists(_root)
            ? Directory.EnumerateDirectories(_root, ".*.staging", SearchOption.TopDirectoryOnly)
                .Where(static path =>
                    !IsReparsePoint(path)
                    && IsValidStagingDirectoryName(Path.GetFileName(path)))
                .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
                .Select(path => new JournalLoadProblem(
                    JournalLoadProblemKind.TemporaryFilePresent,
                    "An unpublished journal staging directory was ignored.",
                    FileName: Path.GetFileName(path)))
                .ToList()
            : [];

    private JournalLoadProblem? FindUnexpectedRootEntry()
    {
        if (!Directory.Exists(_root))
        {
            return null;
        }

        foreach (string path in Directory.EnumerateFileSystemEntries(
                     _root,
                     "*",
                     SearchOption.TopDirectoryOnly)
                 .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            bool recognizedDirectory = Directory.Exists(path)
                && !IsReparsePoint(path)
                && (TryFingerprint(name, out _) || IsValidStagingDirectoryName(name));
            if (!recognizedDirectory)
            {
                return new JournalLoadProblem(
                    JournalLoadProblemKind.UnexpectedDurableEntry,
                    "The journal root contains an unrecognized durable entry.",
                    FileName: name);
            }
        }

        return null;
    }

    private static bool IsValidStagingDirectoryName(string name)
    {
        const string suffix = ".staging";
        int expectedLength = 1 + PlanFingerprint.HexLength + 1 + 32 + suffix.Length;
        if (name.Length != expectedLength
            || name[0] != '.'
            || name[1 + PlanFingerprint.HexLength] != '.'
            || !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        return IsLowerHex(name.AsSpan(1, PlanFingerprint.HexLength))
            && IsLowerHex(name.AsSpan(2 + PlanFingerprint.HexLength, 32));
    }

    private static bool IsValidTemporaryFileName(string name)
    {
        const string tempSuffix = ".tmp";
        if (!name.StartsWith('.') || !name.EndsWith(tempSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        int identifierEnd = name.Length - tempSuffix.Length;
        int identifierStart = name.LastIndexOf('.', identifierEnd - 1);
        if (identifierStart <= 1
            || identifierEnd - identifierStart - 1 != 32
            || !IsLowerHex(name.AsSpan(identifierStart + 1, 32)))
        {
            return false;
        }

        string finalName = name[1..identifierStart];
        return StringComparer.Ordinal.Equals(finalName, ManifestFileName)
            || TryParseRecordFileName(finalName, out _);
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static string RecordFileName(long sequence) =>
        sequence.ToString("D" + SequenceDigits, CultureInfo.InvariantCulture) + RecordSuffix;

    private static bool TryParseRecordFileName(string fileName, out long sequence)
    {
        sequence = -1;
        if (fileName.Length != SequenceDigits + RecordSuffix.Length
            || !fileName.EndsWith(RecordSuffix, StringComparison.Ordinal)
            || fileName.AsSpan(0, SequenceDigits).ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        return long.TryParse(
            fileName.AsSpan(0, SequenceDigits),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out sequence)
            && sequence > 0;
    }

    private static bool TryFingerprint(string value, out PlanFingerprint? fingerprint)
    {
        try
        {
            fingerprint = new PlanFingerprint(value);
            return StringComparer.Ordinal.Equals(fingerprint.Value, value);
        }
        catch (ArgumentException)
        {
            fingerprint = null;
            return false;
        }
    }

    private static void ValidateStart(
        ExecutionManifest manifest,
        ExecutionJournalRecord startRecord)
    {
        if (!startRecord.Fingerprint.Equals(manifest.Fingerprint)
            || startRecord.Sequence != 0
            || startRecord.Kind != ExecutionJournalRecordKind.ExecutionStarted)
        {
            throw new ArgumentException(
                "Begin requires the matching sequence-zero execution-started record.",
                nameof(startRecord));
        }
    }

    private static JournalLoadResult Failure(
        JournalLoadProblemKind kind,
        string description,
        long? sequence = null,
        string? fileName = null) =>
        new(null, [new JournalLoadProblem(kind, description, sequence, fileName)]);

    internal static ExecutionJournalReadResult MapForReplay(JournalLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ExecutionJournalReadProblem[] problems = result.Problems
            .Select(static problem => new ExecutionJournalReadProblem(
                MapProblemKind(problem.Kind),
                problem.RecordSequence))
            .ToArray();
        ExecutionJournalReadStatus status = result.Status switch
        {
            JournalLoadStatus.NotFound => ExecutionJournalReadStatus.NotFound,
            JournalLoadStatus.Loaded => ExecutionJournalReadStatus.Loaded,
            JournalLoadStatus.Invalid => ExecutionJournalReadStatus.Invalid,
            _ => throw new InvalidOperationException("The journal loader returned an unknown status."),
        };

        return new ExecutionJournalReadResult(status, result.Snapshot, problems);
    }

    private static JournalBeginResult InvalidBegin(
        PlanFingerprint fingerprint,
        JournalLoadResult result)
    {
        ExecutionJournalReadResult mapped = MapForReplay(result);
        if (mapped.Status != ExecutionJournalReadStatus.Invalid)
        {
            throw new InvalidOperationException(
                "Only an invalid durable journal can block begin without a trusted snapshot.");
        }

        return JournalBeginResult.InvalidExistingJournal(fingerprint, mapped.Problems);
    }

    private static bool IsExpectedPersistenceFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static bool IsInvalidStoredRecordFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or ArgumentException
            or FormatException
            or OverflowException;

    private static ExecutionJournalReadProblemKind MapProblemKind(
        JournalLoadProblemKind kind) => kind switch
        {
            JournalLoadProblemKind.MissingManifest =>
                ExecutionJournalReadProblemKind.ManifestMissing,
            JournalLoadProblemKind.MalformedManifest =>
                ExecutionJournalReadProblemKind.ManifestMalformed,
            JournalLoadProblemKind.UnsupportedJournalVersion =>
                ExecutionJournalReadProblemKind.UnsupportedFormat,
            JournalLoadProblemKind.WrongFingerprint =>
                ExecutionJournalReadProblemKind.FingerprintOwnershipMismatch,
            JournalLoadProblemKind.MalformedRecord =>
                ExecutionJournalReadProblemKind.RecordMalformed,
            JournalLoadProblemKind.DuplicateSequence
                or JournalLoadProblemKind.SequenceGap
                or JournalLoadProblemKind.RecordFileNameMismatch =>
                    ExecutionJournalReadProblemKind.SequenceViolation,
            JournalLoadProblemKind.PayloadHashMismatch =>
                ExecutionJournalReadProblemKind.PayloadIntegrityViolation,
            JournalLoadProblemKind.PreviousHashMismatch =>
                ExecutionJournalReadProblemKind.HashChainViolation,
            JournalLoadProblemKind.WrongRunOwnership =>
                ExecutionJournalReadProblemKind.RunOwnershipMismatch,
            JournalLoadProblemKind.RecordAfterTerminal =>
                ExecutionJournalReadProblemKind.TerminalTail,
            JournalLoadProblemKind.TemporaryFilePresent =>
                ExecutionJournalReadProblemKind.TemporaryArtifactIgnored,
            JournalLoadProblemKind.UnexpectedDurableEntry =>
                ExecutionJournalReadProblemKind.UnexpectedStorageEntry,
            JournalLoadProblemKind.UnsafeJournalPath =>
                ExecutionJournalReadProblemKind.UnsafeStorage,
            JournalLoadProblemKind.IoFailure =>
                ExecutionJournalReadProblemKind.StorageReadFailure,
            _ => throw new InvalidOperationException(
                "The journal loader returned an unmapped validation problem."),
        };

    private static void TryDeleteStagingDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
