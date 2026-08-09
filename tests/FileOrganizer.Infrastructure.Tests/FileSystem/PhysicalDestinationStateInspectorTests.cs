using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Infrastructure.FileSystem;

namespace FileOrganizer.Infrastructure.Tests.FileSystem;

/// <summary>Physical reads are confined to one unique system-temp directory.</summary>
public sealed class PhysicalDestinationStateInspectorTests
{
    private readonly PathPolicy _paths = DestinationInspectorTestPreparation.HostPathPolicy();

    public PhysicalDestinationStateInspectorTests() =>
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "The M3A physical adapter is intentionally Windows-only.");

    [Fact]
    public void ReportsAnExistingDestinationDirectoryAndMissingFileTarget()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temp.FullPath, "inbox");
        string source = temp.WriteFile(Path.Combine("inbox", "report.txt"), "source");
        string destination = Path.Combine(temp.FullPath, "archive");
        Directory.CreateDirectory(destination);
        OrganizationPlanPreparation preparation = DestinationInspectorTestPreparation.Create(
            _paths,
            sourceRoot,
            destination);
        PhysicalDestinationStateInspector inspector = new();

        DestinationStateSnapshot snapshot = inspector.Inspect(
            preparation,
            TestContext.Current.CancellationToken);

        CanonicalPath destinationPath = Assert.Single(preparation.DestinationDirectories);
        CanonicalPath targetPath = Assert.Single(preparation.ProposedTargetPaths);
        Assert.Equal(
            DestinationEntryKind.Directory,
            Fact(snapshot, destinationPath).EntryKind);
        Assert.Equal(DestinationEntryKind.Missing, Fact(snapshot, targetPath).EntryKind);
        Assert.Equal("source", File.ReadAllText(source));
        Assert.True(Directory.Exists(destination));
        Assert.False(File.Exists(targetPath.Value));
    }

    [Fact]
    public void ReportsAnExistingFileTargetWithoutChangingIt()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temp.FullPath, "inbox");
        temp.WriteFile(Path.Combine("inbox", "report.txt"), "source");
        string destination = Path.Combine(temp.FullPath, "archive");
        string existing = temp.WriteFile(Path.Combine("archive", "report.txt"), "existing");
        OrganizationPlanPreparation preparation = DestinationInspectorTestPreparation.Create(
            _paths,
            sourceRoot,
            destination);
        PhysicalDestinationStateInspector inspector = new();

        DestinationStateSnapshot snapshot = inspector.Inspect(
            preparation,
            TestContext.Current.CancellationToken);

        CanonicalPath targetPath = Assert.Single(preparation.ProposedTargetPaths);
        Assert.Equal(DestinationEntryKind.File, Fact(snapshot, targetPath).EntryKind);
        Assert.Equal("existing", File.ReadAllText(existing));
    }

    [Fact]
    public void ReportsAnExistingDirectoryAtAProposedFileTarget()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string sourceRoot = Path.Combine(temp.FullPath, "inbox");
        temp.WriteFile(Path.Combine("inbox", "report.txt"), "source");
        string destination = Path.Combine(temp.FullPath, "archive");
        string directoryAtTarget = Path.Combine(destination, "report.txt");
        Directory.CreateDirectory(directoryAtTarget);
        OrganizationPlanPreparation preparation = DestinationInspectorTestPreparation.Create(
            _paths,
            sourceRoot,
            destination);
        PhysicalDestinationStateInspector inspector = new();

        DestinationStateSnapshot snapshot = inspector.Inspect(
            preparation,
            TestContext.Current.CancellationToken);

        CanonicalPath targetPath = Assert.Single(preparation.ProposedTargetPaths);
        Assert.Equal(DestinationEntryKind.Directory, Fact(snapshot, targetPath).EntryKind);
        Assert.True(Directory.Exists(directoryAtTarget));
    }

    private static DestinationPathFact Fact(
        DestinationStateSnapshot snapshot,
        CanonicalPath path) =>
        Assert.Single(snapshot.Facts, fact => fact.Path.Value == path.Value);
}
