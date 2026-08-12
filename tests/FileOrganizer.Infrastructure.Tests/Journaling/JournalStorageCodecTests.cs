using System.Text;
using System.Text.Json;
using FileOrganizer.Infrastructure.Journaling;

namespace FileOrganizer.Infrastructure.Tests.Journaling;

public sealed class JournalStorageCodecTests
{
    [Fact]
    public void GenesisRoundTripPreservesCanonicalHashAndImmutableItems()
    {
        StoredJournalGenesis original = JournalStorageTestData.Genesis();

        StoredJournalGenesis restored = JournalStorageCodec.DecodeGenesis(
            JournalStorageCodec.EncodeGenesis(original));

        Assert.Equal(original.ManifestHash, restored.ManifestHash);
        Assert.True(JournalCanonicalHash.HasValidManifestHash(restored));
        Assert.True(JournalCanonicalHash.HasValidPayloadHash(restored.StartRecord));
        Assert.Equal(original.RunId, restored.RunId);
        Assert.Equal(original.Manifest.SourceRootPath, restored.Manifest.SourceRootPath);
        Assert.Equal(original.Manifest.Items[0], Assert.Single(restored.Manifest.Items));
        Assert.Throws<NotSupportedException>(
            () => ((IList<StoredManifestItem>)restored.Manifest.Items).Clear());
    }

    [Fact]
    public void RecordRoundTripPreservesPayloadAndHashes()
    {
        StoredJournalGenesis genesis = JournalStorageTestData.Genesis();
        StoredJournalRecord original = JournalStorageTestData.MoveIntent(
            genesis.StartRecord.PayloadHash);

        StoredJournalRecord restored = JournalStorageCodec.DecodeRecord(
            JournalStorageCodec.EncodeRecord(original));

        Assert.Equal(original, restored);
        Assert.True(JournalCanonicalHash.HasValidPayloadHash(restored));
        Assert.Equal(genesis.StartRecord.PayloadHash, restored.Payload.PreviousRecordHash);
    }

    [Fact]
    public void JsonLineEndingsDoNotAffectCanonicalIntegrity()
    {
        StoredJournalGenesis original = JournalStorageTestData.Genesis();
        string lf = Encoding.UTF8.GetString(JournalStorageCodec.EncodeGenesis(original));
        string crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        StoredJournalGenesis restored = JournalStorageCodec.DecodeGenesis(
            Encoding.UTF8.GetBytes(crlf));

        Assert.Equal(original.ManifestHash, restored.ManifestHash);
        Assert.True(JournalCanonicalHash.HasValidManifestHash(restored));
    }

    [Fact]
    public void JsonPropertyOrderDoesNotAffectCanonicalIntegrity()
    {
        StoredJournalGenesis genesis = JournalStorageTestData.Genesis();
        StoredJournalRecord original = JournalStorageTestData.MoveIntent(
            genesis.StartRecord.PayloadHash);
        byte[] reordered = ReorderRecordProperties(
            JournalStorageCodec.EncodeRecord(original));

        StoredJournalRecord restored = JournalStorageCodec.DecodeRecord(reordered);

        Assert.Equal(original.PayloadHash, restored.PayloadHash);
        Assert.True(JournalCanonicalHash.HasValidPayloadHash(restored));
    }

    [Fact]
    public void DecoderRejectsDuplicateAndUnknownProperties()
    {
        byte[] valid = JournalStorageCodec.EncodeRecord(
            JournalStorageTestData.Genesis().StartRecord);
        string json = Encoding.UTF8.GetString(valid);
        string duplicate = json.Replace(
            "{",
            "{\"journalFormatVersion\":1,",
            StringComparison.Ordinal);
        string unknown = json.Replace(
            "{",
            "{\"unexpected\":true,",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(
            () => JournalStorageCodec.DecodeRecord(Encoding.UTF8.GetBytes(duplicate)));
        Assert.Throws<JsonException>(
            () => JournalStorageCodec.DecodeRecord(Encoding.UTF8.GetBytes(unknown)));
    }

    [Fact]
    public void DecoderRejectsNonCanonicalHashAndIdentifierSpellings()
    {
        byte[] valid = JournalStorageCodec.EncodeRecord(
            JournalStorageTestData.Genesis().StartRecord);
        string json = Encoding.UTF8.GetString(valid);
        string uppercaseHash = json.Replace(
            JournalStorageTestData.Genesis().StartRecord.PayloadHash.Value,
            JournalStorageTestData.Genesis().StartRecord.PayloadHash.Value.ToUpperInvariant(),
            StringComparison.Ordinal);
        string uppercaseRun = json.Replace(
            JournalStorageTestData.RunId,
            JournalStorageTestData.RunId.ToUpperInvariant(),
            StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(
            () => JournalStorageCodec.DecodeRecord(Encoding.UTF8.GetBytes(uppercaseHash)));
        Assert.Throws<ArgumentException>(
            () => JournalStorageCodec.DecodeRecord(Encoding.UTF8.GetBytes(uppercaseRun)));
    }

    private static byte[] ReorderRecordProperties(byte[] jsonBytes)
    {
        using JsonDocument document = JsonDocument.Parse(jsonBytes);
        JsonElement root = document.RootElement;
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("payloadHash");
            writer.WriteStringValue(root.GetProperty("payloadHash").GetString());
            writer.WritePropertyName("payload");
            root.GetProperty("payload").WriteTo(writer);
            writer.WriteNumber(
                "canonicalHashVersion",
                root.GetProperty("canonicalHashVersion").GetInt32());
            writer.WriteNumber(
                "journalFormatVersion",
                root.GetProperty("journalFormatVersion").GetInt32());
            writer.WriteEndObject();
        }

        return output.ToArray();
    }
}
