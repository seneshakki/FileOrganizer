using FileOrganizer.Application.Journaling;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Infrastructure.Journaling;

namespace FileOrganizer.Infrastructure.Tests.Journaling;

internal static class JournalStorageTestData
{
    internal const string Fingerprint =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    internal const string RunId = "0123456789abcdef0123456789abcdef";

    internal static StoredJournalGenesis Genesis(string ruleName = "documents")
    {
        StoredExecutionManifest manifest = Manifest(ruleName);
        StoredJournalRecord start = JournalCanonicalHash.CreateRecord(
            new StoredJournalRecordPayload(
                Fingerprint,
                RunId,
                0,
                previousRecordHash: null,
                UtcTicks(2026, 8, 12, 9, 0, 0),
                ExecutionJournalRecordKind.ExecutionStarted,
                operationOrdinal: null,
                expectedEntryIdentity: null,
                JournalRecordReasonKind.None));
        return JournalCanonicalHash.CreateGenesis(RunId, manifest, start);
    }

    internal static StoredJournalRecord MoveIntent(
        JournalHash previousHash,
        long recordedAtUtcTicks = 639011304010000000) =>
        JournalCanonicalHash.CreateRecord(
            new StoredJournalRecordPayload(
                Fingerprint,
                RunId,
                1,
                previousHash,
                recordedAtUtcTicks,
                ExecutionJournalRecordKind.MoveIntent,
                0,
                File("source-file"),
                JournalRecordReasonKind.None));

    internal static StoredExecutionManifest Manifest(string ruleName = "documents") => new(
        Fingerprint,
        fingerprintFormatVersion: 2,
        UtcTicks(2026, 8, 12, 8, 59, 0),
        new PathSemantics(
            PathSyntax.Windows,
            PathCaseSensitivity.Insensitive,
            PathUnicodeNormalization.FormC),
        @"C:\Inbox",
        Directory("inbox-directory"),
        [
            new StoredManifestItem(
                0,
                OrganizationActionKind.MoveToDirectory,
                @"C:\Inbox\résumé.txt",
                File("source-file"),
                123,
                sourceCreatedUtcTicks: null,
                UtcTicks(2026, 8, 12, 8, 30, 0),
                @"C:\Sorted",
                Directory("sorted-directory"),
                @"C:\Sorted\résumé.txt",
                ruleName,
                selectedRuleOrder: 4),
        ]);

    private static StoredPhysicalIdentity File(string identifier) =>
        new("test-id", 1, "volume-a", identifier, PhysicalEntryKind.RegularFile);

    private static StoredPhysicalIdentity Directory(string identifier) =>
        new("test-id", 1, "volume-a", identifier, PhysicalEntryKind.Directory);

    private static long UtcTicks(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second) => new DateTimeOffset(
            year,
            month,
            day,
            hour,
            minute,
            second,
            TimeSpan.Zero).Ticks;
}
