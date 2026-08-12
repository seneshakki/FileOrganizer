using System.Globalization;
using System.Text;
using FileOrganizer.Application.Journaling;
using FileOrganizer.Infrastructure.Journaling;

namespace FileOrganizer.Infrastructure.Tests.Journaling;

public sealed class JournalCanonicalHashTests
{
    [Fact]
    public void SameLogicalManifestAndRecordHashIdenticallyAcrossRepeatedCalls()
    {
        StoredJournalGenesis first = JournalStorageTestData.Genesis();
        StoredJournalGenesis second = JournalStorageTestData.Genesis();
        StoredJournalRecord firstIntent = JournalStorageTestData.MoveIntent(
            first.StartRecord.PayloadHash);
        StoredJournalRecord secondIntent = JournalStorageTestData.MoveIntent(
            second.StartRecord.PayloadHash);

        Assert.Equal(first.ManifestHash, second.ManifestHash);
        Assert.Equal(first.StartRecord.PayloadHash, second.StartRecord.PayloadHash);
        Assert.Equal(firstIntent.PayloadHash, secondIntent.PayloadHash);
    }

    [Fact]
    public void CultureDoesNotAffectCanonicalHashes()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            JournalHash turkish = JournalStorageTestData.Genesis().ManifestHash;

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            JournalHash japanese = JournalStorageTestData.Genesis().ManifestHash;

            Assert.Equal(turkish, japanese);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void NullAndEmptyHaveDifferentCanonicalEncodings()
    {
        using CanonicalJournalWriter nullWriter = new();
        nullWriter.WriteOptionalString(null);
        using CanonicalJournalWriter emptyWriter = new();
        emptyWriter.WriteOptionalString(string.Empty);

        Assert.NotEqual(
            JournalHash.Compute(nullWriter.ToArray()),
            JournalHash.Compute(emptyWriter.ToArray()));
    }

    [Fact]
    public void InvalidUtf16CannotCollapseThroughReplacementEncoding()
    {
        using CanonicalJournalWriter writer = new();

        Assert.Throws<EncoderFallbackException>(() => writer.WriteString("\ud800"));
    }

    [Fact]
    public void ChangingOneExecutionCriticalManifestFieldChangesHash()
    {
        JournalHash first = JournalStorageTestData.Genesis("documents").ManifestHash;
        JournalHash changed = JournalStorageTestData.Genesis("documents-v2").ManifestHash;

        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void PreviousRecordHashParticipatesInPayloadIntegrity()
    {
        StoredJournalGenesis genesis = JournalStorageTestData.Genesis();
        StoredJournalRecord first = JournalStorageTestData.MoveIntent(
            genesis.StartRecord.PayloadHash);
        JournalHash differentPrevious = JournalHash.Compute([1, 2, 3]);
        StoredJournalRecord changed = JournalStorageTestData.MoveIntent(differentPrevious);

        Assert.NotEqual(first.PayloadHash, changed.PayloadHash);
        Assert.True(JournalCanonicalHash.HasValidPayloadHash(first));
        Assert.True(JournalCanonicalHash.HasValidPayloadHash(changed));
    }

    [Fact]
    public void AlteredPayloadDoesNotValidateAgainstEmbeddedHash()
    {
        StoredJournalGenesis genesis = JournalStorageTestData.Genesis();
        StoredJournalRecord original = JournalStorageTestData.MoveIntent(
            genesis.StartRecord.PayloadHash);
        StoredJournalRecordPayload alteredPayload = new(
            original.Payload.Fingerprint,
            original.Payload.RunId,
            original.Payload.Sequence,
            original.Payload.PreviousRecordHash,
            original.Payload.RecordedAtUtcTicks + 1,
            original.Payload.Kind,
            original.Payload.OperationOrdinal,
            original.Payload.ExpectedEntryIdentity,
            original.Payload.Reason);
        StoredJournalRecord altered = new(
            original.JournalFormatVersion,
            original.CanonicalHashVersion,
            alteredPayload,
            original.PayloadHash);

        Assert.False(JournalCanonicalHash.HasValidPayloadHash(altered));
    }
}
