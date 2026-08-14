using FileOrganizer.Application.Execution;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching.Conditions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Rules;
using FileOrganizer.Core.Scanning;
using FileOrganizer.Infrastructure.FileSystem;

namespace FileOrganizer.Infrastructure.Tests.FileSystem;

internal static class WindowsPhysicalMoveTestPlan
{
    internal static ExecutionManifest Create(
        string sourceRootPath,
        string sourcePath,
        string destinationPath)
    {
        OrganizationPlan plan = CreatePlan(
            sourceRootPath,
            [sourcePath],
            destinationPath);
        return Assert.IsType<ExecutionManifest>(
            ExecutionManifestCompiler.Compile(plan).Manifest);
    }

    internal static OrganizationPlan CreatePlan(
        string sourceRootPath,
        IEnumerable<string> sourcePaths,
        string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        var policy = new PathPolicy(
            PathSyntax.Windows,
            PathCaseSensitivity.Insensitive,
            PathUnicodeNormalization.None);
        var observer = new WindowsPhysicalIdentityAdapter();

        CanonicalPath sourceRoot = Canonical(policy, sourceRootPath);
        CanonicalPath destination = Canonical(policy, destinationPath);

        PhysicalEntryObservation rootObservation = observer.Observe(sourceRoot);
        PhysicalEntryObservation destinationObservation = observer.Observe(destination);

        Assert.Equal(PhysicalEntryObservationOutcome.Success, rootObservation.Outcome);
        Assert.Equal(PhysicalEntryObservationOutcome.Success, destinationObservation.Outcome);

        FileEntry[] files = sourcePaths.Select(
            sourcePath =>
            {
                CanonicalPath source = Canonical(policy, sourcePath);
                PhysicalEntryObservation sourceObservation = observer.Observe(source);
                Assert.Equal(
                    PhysicalEntryObservationOutcome.Success,
                    sourceObservation.Outcome);
                PhysicalFileMetadata metadata = Assert.IsType<PhysicalFileMetadata>(
                    sourceObservation.FileMetadata);
                return new FileEntry(
                    source,
                    metadata.Length,
                    metadata.CreatedUtc,
                    metadata.ModifiedUtc,
                    sourceObservation.Identity);
            }).ToArray();
        var scan = new FileScanResult(
            sourceRoot,
            files,
            [],
            rootObservation.Identity);

        var rule = new Rule(
            "move",
            new ExtensionCondition([".txt"]));
        var rules = new OrganizationRuleSet(
        [
            new OrganizationRule(
                rule,
                new MoveToDirectoryAction(destination.Value)),
        ]);
        OrganizationPlanPreparation preparation = new OrganizationPlanner(
            new FixedTimeProvider(DateTimeOffset.UnixEpoch)).Prepare(
                scan,
                rules,
                policy);
        var snapshot = new DestinationStateSnapshot(
            policy.Semantics,
            preparation.DestinationPaths.Select(
                path => policy.AreEquivalent(path, destination)
                    ? new DestinationPathFact(
                        path,
                        DestinationEntryKind.Directory,
                        physicalIdentity: destinationObservation.Identity)
                    : new DestinationPathFact(path, DestinationEntryKind.Missing)));

        OrganizationPlan plan = OrganizationPlanner.Complete(preparation, snapshot);
        Assert.True(plan.IsEligibleForExecution);
        return plan;
    }

    internal static CanonicalPath Canonical(PathPolicy policy, string path) =>
        Assert.IsType<CanonicalPath>(policy.CanonicalizeAbsolute(path).Path);

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
