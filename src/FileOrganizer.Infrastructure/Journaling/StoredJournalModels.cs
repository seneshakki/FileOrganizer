using System.Collections.ObjectModel;
using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Infrastructure.Journaling;

internal sealed record StoredPhysicalIdentity
{
    internal StoredPhysicalIdentity(
        string scheme,
        int schemeVersion,
        string volumeIdentity,
        string entryIdentifier,
        PhysicalEntryKind entryKind)
    {
        PhysicalEntryIdentity validated = new(
            scheme,
            schemeVersion,
            volumeIdentity,
            entryIdentifier,
            entryKind);
        Scheme = validated.Scheme;
        SchemeVersion = validated.SchemeVersion;
        VolumeIdentity = validated.VolumeIdentity;
        EntryIdentifier = validated.EntryIdentifier;
        EntryKind = validated.EntryKind;
    }

    internal string Scheme { get; }

    internal int SchemeVersion { get; }

    internal string VolumeIdentity { get; }

    internal string EntryIdentifier { get; }

    internal PhysicalEntryKind EntryKind { get; }

    internal static StoredPhysicalIdentity From(PhysicalEntryIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(
            identity.Scheme,
            identity.SchemeVersion,
            identity.VolumeIdentity,
            identity.EntryIdentifier,
            identity.EntryKind);
    }

    internal PhysicalEntryIdentity ToDomain() =>
        new(Scheme, SchemeVersion, VolumeIdentity, EntryIdentifier, EntryKind);
}

internal sealed record StoredManifestItem
{
    internal StoredManifestItem(
        int ordinal,
        OrganizationActionKind actionKind,
        string sourcePath,
        StoredPhysicalIdentity sourceIdentity,
        long sourceLength,
        long? sourceCreatedUtcTicks,
        long sourceModifiedUtcTicks,
        string destinationDirectoryPath,
        StoredPhysicalIdentity destinationDirectoryIdentity,
        string targetPath,
        string selectedRuleName,
        int selectedRuleOrder)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        _ = JournalWireTokens.ToToken(actionKind);
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectoryPath);
        ArgumentNullException.ThrowIfNull(destinationDirectoryIdentity);
        ArgumentException.ThrowIfNullOrEmpty(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedRuleName);

        if (sourceIdentity.EntryKind != PhysicalEntryKind.RegularFile)
        {
            throw new ArgumentException(
                "A stored manifest source identity must identify a regular file.",
                nameof(sourceIdentity));
        }

        if (destinationDirectoryIdentity.EntryKind != PhysicalEntryKind.Directory)
        {
            throw new ArgumentException(
                "A stored destination identity must identify a directory.",
                nameof(destinationDirectoryIdentity));
        }

        Ordinal = ordinal;
        ActionKind = actionKind;
        SourcePath = sourcePath;
        SourceIdentity = sourceIdentity;
        SourceLength = sourceLength;
        SourceCreatedUtcTicks = sourceCreatedUtcTicks;
        SourceModifiedUtcTicks = sourceModifiedUtcTicks;
        DestinationDirectoryPath = destinationDirectoryPath;
        DestinationDirectoryIdentity = destinationDirectoryIdentity;
        TargetPath = targetPath;
        SelectedRuleName = selectedRuleName;
        SelectedRuleOrder = selectedRuleOrder;
    }

    internal int Ordinal { get; }

    internal OrganizationActionKind ActionKind { get; }

    internal string SourcePath { get; }

    internal StoredPhysicalIdentity SourceIdentity { get; }

    internal long SourceLength { get; }

    internal long? SourceCreatedUtcTicks { get; }

    internal long SourceModifiedUtcTicks { get; }

    internal string DestinationDirectoryPath { get; }

    internal StoredPhysicalIdentity DestinationDirectoryIdentity { get; }

    internal string TargetPath { get; }

    internal string SelectedRuleName { get; }

    internal int SelectedRuleOrder { get; }

    internal ExecutionManifestPersistenceItem ToPersistenceItem() => new(
        Ordinal,
        ActionKind,
        SourcePath,
        SourceIdentity.ToDomain(),
        SourceLength,
        ToUtc(SourceCreatedUtcTicks),
        ToUtc(SourceModifiedUtcTicks),
        DestinationDirectoryPath,
        DestinationDirectoryIdentity.ToDomain(),
        TargetPath,
        SelectedRuleName,
        SelectedRuleOrder);

    private static DateTimeOffset? ToUtc(long? ticks) =>
        ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null;

    private static DateTimeOffset ToUtc(long ticks) =>
        new(ticks, TimeSpan.Zero);
}

internal sealed class StoredExecutionManifest
{
    private readonly ReadOnlyCollection<StoredManifestItem> _items;

    internal StoredExecutionManifest(
        string fingerprint,
        int fingerprintFormatVersion,
        long generatedAtUtcTicks,
        PathSemantics pathSemantics,
        string sourceRootPath,
        StoredPhysicalIdentity sourceRootIdentity,
        IEnumerable<StoredManifestItem> items)
    {
        Fingerprint = JournalIdentifier.RequireFingerprint(fingerprint);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fingerprintFormatVersion);
        _ = JournalWireTokens.ToToken(pathSemantics.Syntax);
        _ = JournalWireTokens.ToToken(pathSemantics.CaseSensitivity);
        _ = JournalWireTokens.ToToken(pathSemantics.UnicodeNormalization);
        ArgumentException.ThrowIfNullOrEmpty(sourceRootPath);
        ArgumentNullException.ThrowIfNull(sourceRootIdentity);
        ArgumentNullException.ThrowIfNull(items);

        if (sourceRootIdentity.EntryKind != PhysicalEntryKind.Directory)
        {
            throw new ArgumentException(
                "A stored source-root identity must identify a directory.",
                nameof(sourceRootIdentity));
        }

        StoredManifestItem[] snapshot = [.. items];
        for (int index = 0; index < snapshot.Length; index++)
        {
            StoredManifestItem item = snapshot[index]
                ?? throw new ArgumentException(
                    "Stored manifest items cannot contain null.",
                    nameof(items));
            if (item.Ordinal != index)
            {
                throw new ArgumentException(
                    "Stored manifest ordinals must be contiguous and deterministically ordered.",
                    nameof(items));
            }
        }

        FingerprintFormatVersion = fingerprintFormatVersion;
        GeneratedAtUtcTicks = generatedAtUtcTicks;
        PathSemantics = pathSemantics;
        SourceRootPath = sourceRootPath;
        SourceRootIdentity = sourceRootIdentity;
        _items = Array.AsReadOnly(snapshot);
    }

    internal string Fingerprint { get; }

    internal int FingerprintFormatVersion { get; }

    internal long GeneratedAtUtcTicks { get; }

    internal PathSemantics PathSemantics { get; }

    internal string SourceRootPath { get; }

    internal StoredPhysicalIdentity SourceRootIdentity { get; }

    internal IReadOnlyList<StoredManifestItem> Items => _items;

    internal static StoredExecutionManifest From(ExecutionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        StoredManifestItem[] items = manifest.Items.Select(
            item => new StoredManifestItem(
                item.Ordinal,
                item.ActionKind,
                item.SourcePrecondition.CanonicalPath.Value,
                StoredPhysicalIdentity.From(item.SourcePrecondition.PhysicalIdentity!),
                item.SourcePrecondition.Length,
                item.SourcePrecondition.CreatedUtc?.UtcDateTime.Ticks,
                item.SourcePrecondition.ModifiedUtc.UtcDateTime.Ticks,
                item.DestinationDirectory.Value,
                StoredPhysicalIdentity.From(item.DestinationDirectoryPhysicalIdentity),
                item.TargetPath.Value,
                item.SelectedRuleName,
                item.SelectedRuleOrder)).ToArray();

        return new(
            manifest.Fingerprint.Value,
            ExecutionManifest.FingerprintFormatVersion,
            manifest.GeneratedAtUtc.UtcDateTime.Ticks,
            manifest.PathSemantics,
            manifest.SourceRoot.Value,
            StoredPhysicalIdentity.From(manifest.SourceRootPhysicalIdentity),
            items);
    }

    internal ExecutionManifest ToDomain()
    {
        if (FingerprintFormatVersion != ExecutionManifest.FingerprintFormatVersion)
        {
            throw new NotSupportedException(
                $"Manifest fingerprint format {FingerprintFormatVersion} is not supported.");
        }

        return ExecutionManifestPersistence.RestoreManifest(
            new PlanFingerprint(Fingerprint),
            new DateTimeOffset(GeneratedAtUtcTicks, TimeSpan.Zero),
            PathSemantics,
            SourceRootPath,
            SourceRootIdentity.ToDomain(),
            Items.Select(item => item.ToPersistenceItem()));
    }
}

internal sealed record StoredJournalRecordPayload
{
    internal StoredJournalRecordPayload(
        string fingerprint,
        string runId,
        long sequence,
        JournalHash? previousRecordHash,
        long recordedAtUtcTicks,
        ExecutionJournalRecordKind kind,
        int? operationOrdinal,
        StoredPhysicalIdentity? expectedEntryIdentity,
        JournalRecordReasonKind reason)
    {
        Fingerprint = JournalIdentifier.RequireFingerprint(fingerprint);
        RunId = JournalIdentifier.RequireRunId(runId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        _ = JournalWireTokens.ToToken(kind);
        _ = JournalWireTokens.ToToken(reason);

        if ((sequence == 0) != (previousRecordHash is null))
        {
            throw new ArgumentException(
                "Only sequence zero omits the previous-record hash.",
                nameof(previousRecordHash));
        }

        if (operationOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operationOrdinal));
        }

        if (expectedEntryIdentity is not null
            && expectedEntryIdentity.EntryKind != PhysicalEntryKind.RegularFile)
        {
            throw new ArgumentException(
                "A stored expected entry identity must identify a regular file.",
                nameof(expectedEntryIdentity));
        }

        Sequence = sequence;
        PreviousRecordHash = previousRecordHash;
        RecordedAtUtcTicks = recordedAtUtcTicks;
        Kind = kind;
        OperationOrdinal = operationOrdinal;
        ExpectedEntryIdentity = expectedEntryIdentity;
        Reason = reason;
    }

    internal string Fingerprint { get; }

    internal string RunId { get; }

    internal long Sequence { get; }

    internal JournalHash? PreviousRecordHash { get; }

    internal long RecordedAtUtcTicks { get; }

    internal ExecutionJournalRecordKind Kind { get; }

    internal int? OperationOrdinal { get; }

    internal StoredPhysicalIdentity? ExpectedEntryIdentity { get; }

    internal JournalRecordReasonKind Reason { get; }

    internal static StoredJournalRecordPayload From(
        ExecutionJournalRecord record,
        JournalHash? previousRecordHash)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new(
            record.Fingerprint.Value,
            record.RunId.Value,
            record.Sequence,
            previousRecordHash,
            record.RecordedAtUtc.UtcDateTime.Ticks,
            record.Kind,
            record.OperationOrdinal,
            record.ExpectedEntryIdentity is null
                ? null
                : StoredPhysicalIdentity.From(record.ExpectedEntryIdentity),
            record.Reason);
    }

    internal ExecutionJournalRecord ToDomain() =>
        ExecutionJournalPersistence.RestoreRecord(
            new ExecutionRunId(RunId),
            new PlanFingerprint(Fingerprint),
            Sequence,
            new DateTimeOffset(RecordedAtUtcTicks, TimeSpan.Zero),
            Kind,
            OperationOrdinal,
            ExpectedEntryIdentity?.ToDomain(),
            Reason);
}

internal sealed record StoredJournalRecord
{
    internal StoredJournalRecord(
        int journalFormatVersion,
        int canonicalHashVersion,
        StoredJournalRecordPayload payload,
        JournalHash payloadHash)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(journalFormatVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(canonicalHashVersion);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(payloadHash);
        JournalFormatVersion = journalFormatVersion;
        CanonicalHashVersion = canonicalHashVersion;
        Payload = payload;
        PayloadHash = payloadHash;
    }

    internal int JournalFormatVersion { get; }

    internal int CanonicalHashVersion { get; }

    internal StoredJournalRecordPayload Payload { get; }

    internal JournalHash PayloadHash { get; }

    internal ExecutionJournalRecord ToDomain() => Payload.ToDomain();
}

internal sealed class StoredJournalGenesis
{
    internal StoredJournalGenesis(
        int journalFormatVersion,
        int canonicalHashVersion,
        string runId,
        StoredExecutionManifest manifest,
        StoredJournalRecord startRecord,
        JournalHash manifestHash)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(journalFormatVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(canonicalHashVersion);
        RunId = JournalIdentifier.RequireRunId(runId);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(startRecord);
        ArgumentNullException.ThrowIfNull(manifestHash);

        if (startRecord.Payload.Sequence != 0
            || startRecord.Payload.Kind != ExecutionJournalRecordKind.ExecutionStarted
            || startRecord.JournalFormatVersion != journalFormatVersion
            || startRecord.CanonicalHashVersion != canonicalHashVersion
            || !StringComparer.Ordinal.Equals(startRecord.Payload.Fingerprint, manifest.Fingerprint)
            || !StringComparer.Ordinal.Equals(startRecord.Payload.RunId, RunId))
        {
            throw new ArgumentException(
                "Journal genesis must contain the matching sequence-zero start record.",
                nameof(startRecord));
        }

        JournalFormatVersion = journalFormatVersion;
        CanonicalHashVersion = canonicalHashVersion;
        Manifest = manifest;
        StartRecord = startRecord;
        ManifestHash = manifestHash;
    }

    internal int JournalFormatVersion { get; }

    internal int CanonicalHashVersion { get; }

    internal string RunId { get; }

    internal StoredExecutionManifest Manifest { get; }

    internal StoredJournalRecord StartRecord { get; }

    internal JournalHash ManifestHash { get; }

    internal ExecutionJournalSnapshot ToSnapshot() => new(
        new ExecutionRunId(RunId),
        Manifest.ToDomain(),
        [StartRecord.ToDomain()]);
}
