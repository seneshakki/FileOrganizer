using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching.Conditions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Rules;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Application.Tests.Execution;

internal static class ManifestTestPlan
{
    internal static readonly DateTimeOffset GeneratedAt =
        new(2026, 08, 09, 09, 10, 11, TimeSpan.Zero);

    internal static readonly PathPolicy Policy = new(
        PathSyntax.Windows,
        PathCaseSensitivity.Insensitive,
        PathUnicodeNormalization.FormC);

    internal static OrganizationPlan Create(
        IEnumerable<TestOperation> operations,
        IEnumerable<ScanDiagnostic>? diagnostics = null,
        DateTimeOffset? generatedAt = null,
        IEnumerable<string>? unmatchedSourcePaths = null,
        PathPolicy? pathPolicy = null,
        string sourceRoot = @"C:\Inbox",
        bool includeSourceRootPhysicalIdentity = true,
        bool includeSourcePhysicalIdentities = true,
        bool includeDestinationDirectoryPhysicalIdentities = true,
        PhysicalEntryIdentity? sourceRootPhysicalIdentity = null,
        Func<int, PhysicalEntryIdentity>? sourcePhysicalIdentityFactory = null,
        Func<int, PhysicalEntryIdentity>? destinationPhysicalIdentityFactory = null)
    {
        PathPolicy selectedPolicy = pathPolicy ?? Policy;
        TestOperation[] snapshot = [.. operations];
        CanonicalPath[] operationPaths = snapshot
            .Select(operation => Path(selectedPolicy, operation.SourcePath))
            .ToArray();
        CanonicalPath[] unmatchedPaths = (unmatchedSourcePaths ?? [])
            .Select(path => Path(selectedPolicy, path))
            .ToArray();
        Dictionary<CanonicalPath, int> sourceOrdinals = operationPaths
            .Concat(unmatchedPaths)
            .OrderBy(path => path, selectedPolicy.DeterministicComparer)
            .Distinct(selectedPolicy.LogicalComparer)
            .Select((path, index) => (path, index))
            .ToDictionary(
                pair => pair.path,
                pair => pair.index,
                selectedPolicy.LogicalComparer);
        FileEntry[] files = snapshot
            .Select(
                (operation, index) => new FileEntry(
                    operationPaths[index],
                    operation.Length,
                    operation.CreatedUtc,
                    operation.ModifiedUtc,
                    includeSourcePhysicalIdentities
                        ? (sourcePhysicalIdentityFactory ?? DefaultSourceIdentity)(
                            sourceOrdinals[operationPaths[index]])
                        : null))
            .Concat(
                unmatchedPaths.Select(
                    path => new FileEntry(
                        path,
                        1,
                        created: null,
                        GeneratedAt.AddDays(-1),
                        includeSourcePhysicalIdentities
                            ? (sourcePhysicalIdentityFactory ?? DefaultSourceIdentity)(
                                sourceOrdinals[path])
                            : null)))
            .ToArray();

        OrganizationRule[] rules = snapshot
            .Select(
                (operation, index) => new OrganizationRule(
                    new Rule(
                        operation.RuleName,
                        new ExtensionCondition([Extension(operation.SourcePath)]),
                        order: operation.RuleOrder ?? index),
                    operation.Action))
            .ToArray();

        FileScanResult scan = new(
            Path(selectedPolicy, sourceRoot),
            files,
            diagnostics ?? [],
            includeSourceRootPhysicalIdentity
                ? sourceRootPhysicalIdentity ?? TestPhysicalIdentities.Directory("scan-root")
                : null);
        OrganizationPlanPreparation preparation = new OrganizationPlanner(
            new FixedTimeProvider(generatedAt ?? GeneratedAt)).Prepare(
                scan,
                new OrganizationRuleSet(rules),
                selectedPolicy);

        HashSet<CanonicalPath> directories = new(
            preparation.DestinationDirectories,
            selectedPolicy.LogicalComparer);
        Dictionary<CanonicalPath, int> destinationOrdinals = preparation.DestinationDirectories
            .Select((path, index) => (path, index))
            .ToDictionary(
                pair => pair.path,
                pair => pair.index,
                selectedPolicy.LogicalComparer);
        DestinationPathFact[] facts = preparation.DestinationPaths
            .Select(
                path => new DestinationPathFact(
                    path,
                    directories.Contains(path)
                        ? DestinationEntryKind.Directory
                        : DestinationEntryKind.Missing,
                    physicalIdentity: directories.Contains(path)
                        && includeDestinationDirectoryPhysicalIdentities
                            ? (destinationPhysicalIdentityFactory
                                ?? DefaultDestinationIdentity)(destinationOrdinals[path])
                            : null))
            .ToArray();

        return OrganizationPlanner.Complete(
            preparation,
            new DestinationStateSnapshot(selectedPolicy.Semantics, facts));
    }

    internal static CanonicalPath Path(string value) =>
        Path(Policy, value);

    internal static CanonicalPath Path(PathPolicy policy, string value) =>
        policy.CanonicalizeAbsolute(value).Path
        ?? throw new InvalidOperationException($"Invalid test path: {value}");

    private static string Extension(string path)
    {
        int dot = path.LastIndexOf('.');
        return dot < 0 ? ExtensionCondition.NoExtension : path[dot..];
    }

    private static PhysicalEntryIdentity DefaultSourceIdentity(int ordinal) =>
        TestPhysicalIdentities.File($"entry-{ordinal}");

    private static PhysicalEntryIdentity DefaultDestinationIdentity(int ordinal) =>
        TestPhysicalIdentities.Directory($"destination-{ordinal}");

    internal sealed record TestOperation(
        string SourcePath,
        OrganizationAction Action,
        string RuleName,
        long Length = 10,
        DateTimeOffset? CreatedUtc = null,
        DateTimeOffset? Modified = null,
        int? RuleOrder = null)
    {
        internal DateTimeOffset ModifiedUtc { get; } =
            Modified ?? GeneratedAt.AddDays(-1);
    }

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
