namespace FileOrganizer.Infrastructure.Journaling;

internal static class JournalCanonicalHash
{
    internal const int JournalFormatVersion = 1;
    internal const int CanonicalHashVersion = 1;

    private const string ManifestDomain = "FileOrganizer.DurableJournal.Manifest";
    private const string RecordDomain = "FileOrganizer.DurableJournal.Record";

    internal static StoredJournalRecord CreateRecord(StoredJournalRecordPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        JournalHash hash = ComputeRecordPayloadHash(
            JournalFormatVersion,
            CanonicalHashVersion,
            payload);
        return new(JournalFormatVersion, CanonicalHashVersion, payload, hash);
    }

    internal static StoredJournalRecord CreateRecord(
        FileOrganizer.Application.Journaling.ExecutionJournalRecord record,
        JournalHash? previousRecordHash) =>
        CreateRecord(StoredJournalRecordPayload.From(record, previousRecordHash));

    internal static JournalHash ComputeRecordPayloadHash(
        int journalFormatVersion,
        int canonicalHashVersion,
        StoredJournalRecordPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using CanonicalJournalWriter writer = new();
        WriteRecordPayload(
            writer,
            journalFormatVersion,
            canonicalHashVersion,
            payload);
        return JournalHash.Compute(writer.ToArray());
    }

    internal static bool HasValidPayloadHash(StoredJournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        JournalHash expected = ComputeRecordPayloadHash(
            record.JournalFormatVersion,
            record.CanonicalHashVersion,
            record.Payload);
        return expected.Equals(record.PayloadHash);
    }

    internal static StoredJournalGenesis CreateGenesis(
        string runId,
        StoredExecutionManifest manifest,
        StoredJournalRecord startRecord)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(startRecord);
        runId = JournalIdentifier.RequireRunId(runId);

        JournalHash hash = ComputeManifestHash(
            JournalFormatVersion,
            CanonicalHashVersion,
            runId,
            manifest,
            startRecord);
        return new(
            JournalFormatVersion,
            CanonicalHashVersion,
            runId,
            manifest,
            startRecord,
            hash);
    }

    internal static StoredJournalGenesis CreateGenesis(
        FileOrganizer.Application.Journaling.ExecutionRunId runId,
        FileOrganizer.Application.Execution.ExecutionManifest manifest,
        FileOrganizer.Application.Journaling.ExecutionJournalRecord startRecord)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(startRecord);

        StoredJournalRecord storedStart = CreateRecord(startRecord, previousRecordHash: null);
        return CreateGenesis(
            runId.Value,
            StoredExecutionManifest.From(manifest),
            storedStart);
    }

    internal static JournalHash ComputeManifestHash(
        int journalFormatVersion,
        int canonicalHashVersion,
        string runId,
        StoredExecutionManifest manifest,
        StoredJournalRecord startRecord)
    {
        runId = JournalIdentifier.RequireRunId(runId);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(startRecord);

        using CanonicalJournalWriter writer = new();
        writer.WriteString(ManifestDomain);
        writer.WriteInt32(canonicalHashVersion);
        writer.WriteInt32(journalFormatVersion);
        writer.WriteLowerHex(
            manifest.Fingerprint,
            JournalIdentifier.FingerprintHexLength,
            "plan fingerprint");
        writer.WriteLowerHex(runId, JournalIdentifier.RunIdHexLength, "execution run identifier");
        writer.WriteInt32(manifest.FingerprintFormatVersion);
        writer.WriteInt64(manifest.GeneratedAtUtcTicks);
        writer.WriteString(JournalWireTokens.ToToken(manifest.PathSemantics.Syntax));
        writer.WriteString(JournalWireTokens.ToToken(manifest.PathSemantics.CaseSensitivity));
        writer.WriteString(JournalWireTokens.ToToken(manifest.PathSemantics.UnicodeNormalization));
        writer.WriteString(manifest.SourceRootPath);
        WritePhysicalIdentity(writer, manifest.SourceRootIdentity);
        writer.WriteInt32(manifest.Items.Count);

        foreach (StoredManifestItem item in manifest.Items)
        {
            writer.WriteInt32(item.Ordinal);
            writer.WriteString(JournalWireTokens.ToToken(item.ActionKind));
            writer.WriteString(item.SourcePath);
            WritePhysicalIdentity(writer, item.SourceIdentity);
            writer.WriteInt64(item.SourceLength);
            writer.WriteOptionalInt64(item.SourceCreatedUtcTicks);
            writer.WriteInt64(item.SourceModifiedUtcTicks);
            writer.WriteString(item.DestinationDirectoryPath);
            WritePhysicalIdentity(writer, item.DestinationDirectoryIdentity);
            writer.WriteString(item.TargetPath);
            writer.WriteString(item.SelectedRuleName);
            writer.WriteInt32(item.SelectedRuleOrder);
        }

        WriteRecordPayload(
            writer,
            startRecord.JournalFormatVersion,
            startRecord.CanonicalHashVersion,
            startRecord.Payload);
        writer.WriteHash(startRecord.PayloadHash);
        return JournalHash.Compute(writer.ToArray());
    }

    internal static bool HasValidManifestHash(StoredJournalGenesis genesis)
    {
        ArgumentNullException.ThrowIfNull(genesis);
        JournalHash expected = ComputeManifestHash(
            genesis.JournalFormatVersion,
            genesis.CanonicalHashVersion,
            genesis.RunId,
            genesis.Manifest,
            genesis.StartRecord);
        return expected.Equals(genesis.ManifestHash);
    }

    private static void WriteRecordPayload(
        CanonicalJournalWriter writer,
        int journalFormatVersion,
        int canonicalHashVersion,
        StoredJournalRecordPayload payload)
    {
        writer.WriteString(RecordDomain);
        writer.WriteInt32(canonicalHashVersion);
        writer.WriteInt32(journalFormatVersion);
        writer.WriteLowerHex(
            payload.Fingerprint,
            JournalIdentifier.FingerprintHexLength,
            "plan fingerprint");
        writer.WriteLowerHex(
            payload.RunId,
            JournalIdentifier.RunIdHexLength,
            "execution run identifier");
        writer.WriteInt64(payload.Sequence);
        writer.WriteOptionalHash(payload.PreviousRecordHash);
        writer.WriteInt64(payload.RecordedAtUtcTicks);
        writer.WriteString(JournalWireTokens.ToToken(payload.Kind));
        writer.WriteOptionalInt32(payload.OperationOrdinal);
        WriteOptionalPhysicalIdentity(writer, payload.ExpectedEntryIdentity);
        writer.WriteString(JournalWireTokens.ToToken(payload.Reason));
    }

    private static void WriteOptionalPhysicalIdentity(
        CanonicalJournalWriter writer,
        StoredPhysicalIdentity? identity)
    {
        writer.WriteByte(identity is null ? (byte)0 : (byte)1);
        if (identity is not null)
        {
            WritePhysicalIdentity(writer, identity);
        }
    }

    private static void WritePhysicalIdentity(
        CanonicalJournalWriter writer,
        StoredPhysicalIdentity identity)
    {
        writer.WriteString(identity.Scheme);
        writer.WriteInt32(identity.SchemeVersion);
        writer.WriteString(identity.VolumeIdentity);
        writer.WriteString(identity.EntryIdentifier);
        writer.WriteString(JournalWireTokens.ToToken(identity.EntryKind));
    }
}
