using FileOrganizer.Application.Execution;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching.Conditions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Rules;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Infrastructure.Tests.Journaling;

internal static class JournalStoreTestManifest
{
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

    internal static ExecutionManifest Create(
        string token = "a",
        DateTimeOffset? generatedAt = null,
        string? ruleName = null)
    {
        PathPolicy policy = new(
            PathSyntax.Windows,
            PathCaseSensitivity.Insensitive,
            PathUnicodeNormalization.FormC);
        CanonicalPath root = Path(policy, @"C:\Inbox");
        CanonicalPath source = Path(policy, $@"C:\Inbox\source-{token}.txt");
        PhysicalEntryIdentity sourceIdentity = File("source-" + token);
        FileScanResult scan = new(
            root,
            [new FileEntry(source, 12, null, GeneratedAt.AddMinutes(-1), sourceIdentity)],
            [],
            Directory("root-" + token));
        OrganizationRuleSet rules = new(
            [
                new OrganizationRule(
                    new Rule(
                        ruleName ?? "text-" + token,
                        new ExtensionCondition([".txt"]),
                        order: 0),
                    new MoveToDirectoryAction(@"C:\Sorted")),
            ]);
        OrganizationPlanPreparation preparation = new OrganizationPlanner(
            new FixedTimeProvider(generatedAt ?? GeneratedAt)).Prepare(scan, rules, policy);
        CanonicalPath destination = Assert.Single(preparation.DestinationDirectories);
        DestinationStateSnapshot facts = new(
            policy.Semantics,
            preparation.DestinationPaths.Select(
                path => new DestinationPathFact(
                    path,
                    policy.AreEquivalent(path, destination)
                        ? DestinationEntryKind.Directory
                        : DestinationEntryKind.Missing,
                    physicalIdentity: policy.AreEquivalent(path, destination)
                        ? Directory("destination-" + token)
                        : null)));
        OrganizationPlan plan = OrganizationPlanner.Complete(preparation, facts);
        return Assert.IsType<ExecutionManifest>(ExecutionManifestCompiler.Compile(plan).Manifest);
    }

    internal static PhysicalEntryIdentity File(string id) =>
        new("test-id", 1, "test-volume", id, PhysicalEntryKind.RegularFile);

    internal static PhysicalEntryIdentity Directory(string id) =>
        new("test-id", 1, "test-volume", id, PhysicalEntryKind.Directory);

    private static CanonicalPath Path(PathPolicy policy, string value) =>
        policy.CanonicalizeAbsolute(value).Path!;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
