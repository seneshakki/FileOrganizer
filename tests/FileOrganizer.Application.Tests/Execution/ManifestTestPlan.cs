using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching.Conditions;
using FileOrganizer.Core.Paths;
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
        string sourceRoot = @"C:\Inbox")
    {
        PathPolicy selectedPolicy = pathPolicy ?? Policy;
        TestOperation[] snapshot = [.. operations];
        FileEntry[] files = snapshot
            .Select(
                operation => new FileEntry(
                    Path(selectedPolicy, operation.SourcePath),
                    operation.Length,
                    operation.CreatedUtc,
                    operation.ModifiedUtc))
            .Concat(
                (unmatchedSourcePaths ?? []).Select(
                    path => new FileEntry(
                        Path(selectedPolicy, path),
                        1,
                        created: null,
                        GeneratedAt.AddDays(-1))))
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
            diagnostics ?? []);
        OrganizationPlanPreparation preparation = new OrganizationPlanner(
            new FixedTimeProvider(generatedAt ?? GeneratedAt)).Prepare(
                scan,
                new OrganizationRuleSet(rules),
                selectedPolicy);

        HashSet<CanonicalPath> directories = new(
            preparation.DestinationDirectories,
            selectedPolicy.LogicalComparer);
        DestinationPathFact[] facts = preparation.DestinationPaths
            .Select(
                path => new DestinationPathFact(
                    path,
                    directories.Contains(path)
                        ? DestinationEntryKind.Directory
                        : DestinationEntryKind.Missing))
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
