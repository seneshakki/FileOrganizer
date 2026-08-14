using FileOrganizer.Application.Execution;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Matching;
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
        var policy = new PathPolicy(
            PathSyntax.Windows,
            PathCaseSensitivity.Insensitive,
            PathUnicodeNormalization.None);
        var observer = new WindowsPhysicalIdentityAdapter();

        CanonicalPath sourceRoot = Canonical(policy, sourceRootPath);
        CanonicalPath source = Canonical(policy, sourcePath);
        CanonicalPath destination = Canonical(policy, destinationPath);

        PhysicalEntryObservation rootObservation = observer.Observe(sourceRoot);
        PhysicalEntryObservation sourceObservation = observer.Observe(source);
        PhysicalEntryObservation destinationObservation = observer.Observe(destination);

        Assert.Equal(PhysicalEntryObservationOutcome.Success, rootObservation.Outcome);
        Assert.Equal(PhysicalEntryObservationOutcome.Success, sourceObservation.Outcome);
        Assert.Equal(PhysicalEntryObservationOutcome.Success, destinationObservation.Outcome);

        PhysicalFileMetadata metadata = Assert.IsType<PhysicalFileMetadata>(
            sourceObservation.FileMetadata);
        var scan = new FileScanResult(
            sourceRoot,
            [
                new FileEntry(
                    source,
                    metadata.Length,
                    metadata.CreatedUtc,
                    metadata.ModifiedUtc,
                    sourceObservation.Identity),
            ],
            [],
            rootObservation.Identity);

        var rule = new Rule(
            "move",
            new NameCondition(
                NameTarget.FullName,
                TextOperator.Equals,
                source.FileName!,
                CaseSensitivity.Insensitive));
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
        CanonicalPath target = Assert.IsType<CanonicalPath>(
            policy.AppendFileName(destination, source.FileName).Path);
        var snapshot = new DestinationStateSnapshot(
            policy.Semantics,
            [
                new DestinationPathFact(
                    destination,
                    DestinationEntryKind.Directory,
                    physicalIdentity: destinationObservation.Identity),
                new DestinationPathFact(target, DestinationEntryKind.Missing),
            ]);

        OrganizationPlan plan = OrganizationPlanner.Complete(preparation, snapshot);
        Assert.True(plan.IsEligibleForExecution);
        return Assert.IsType<ExecutionManifest>(ExecutionManifestCompiler.Compile(plan).Manifest);
    }

    internal static CanonicalPath Canonical(PathPolicy policy, string path) =>
        Assert.IsType<CanonicalPath>(policy.CanonicalizeAbsolute(path).Path);

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
