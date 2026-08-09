using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Infrastructure.FileSystem;

namespace FileOrganizer.Infrastructure.Tests.FileSystem;

public sealed class PhysicalDestinationStateInspectorFailureTests
{
    private readonly PathPolicy _paths = DestinationInspectorTestPreparation.HostPathPolicy();

    [Fact]
    public void ClassifiesFilesAndDirectoriesWithoutAssumingEveryRequestIsAFileTarget()
    {
        OrganizationPlanPreparation preparation = Preparation();
        CanonicalPath directory = Assert.Single(preparation.DestinationDirectories);
        CanonicalPath target = Assert.Single(preparation.ProposedTargetPaths);
        FakeReadOnlyFileSystemProbe probe = new()
        {
            GetAttributesHandler = path => path == directory.Value
                ? FileAttributes.Directory
                : FileAttributes.Normal,
        };
        PhysicalDestinationStateInspector inspector = new(probe);

        DestinationStateSnapshot snapshot = inspector.Inspect(
            preparation,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.Facts.Count);
        Assert.Equal(
            DestinationEntryKind.Directory,
            Fact(snapshot, directory).EntryKind);
        Assert.Equal(DestinationEntryKind.File, Fact(snapshot, target).EntryKind);
        Assert.All(snapshot.Facts, fact => Assert.Null(fact.Failure));
        Assert.Equal(4, probe.AttributeReadCount);
    }

    [Fact]
    public void MissingPathsAreExplicitFactsAndAreNotRecheckedAsExistingEntries()
    {
        OrganizationPlanPreparation preparation = Preparation();
        FakeReadOnlyFileSystemProbe probe = new()
        {
            GetAttributesHandler = _ => throw new FileNotFoundException(),
        };
        PhysicalDestinationStateInspector inspector = new(probe);

        DestinationStateSnapshot snapshot = inspector.Inspect(
            preparation,
            TestContext.Current.CancellationToken);

        Assert.All(
            snapshot.Facts,
            fact => Assert.Equal(DestinationEntryKind.Missing, fact.EntryKind));
        Assert.All(snapshot.Facts, fact => Assert.Null(fact.Failure));
        Assert.Equal(preparation.DestinationPaths.Count, probe.AttributeReadCount);
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint)]
    [InlineData(FileAttributes.Device)]
    public void UnsupportedEntryTypesFailClosed(FileAttributes attributes)
    {
        OrganizationPlanPreparation preparation = Preparation();
        FakeReadOnlyFileSystemProbe probe = new()
        {
            GetAttributesHandler = _ => attributes,
        };
        PhysicalDestinationStateInspector inspector = new(probe);

        DestinationStateSnapshot snapshot = inspector.Inspect(
            preparation,
            TestContext.Current.CancellationToken);

        Assert.All(
            snapshot.Facts,
            fact =>
            {
                Assert.Equal(DestinationEntryKind.InspectionFailed, fact.EntryKind);
                Assert.Equal(
                    DestinationInspectionFailureKind.UnsupportedEntry,
                    Assert.IsType<DestinationInspectionFailure>(fact.Failure).Kind);
            });
    }

    [Fact]
    public void DetectsAnObservableTypeChangeBetweenAttributeReads()
    {
        OrganizationPlanPreparation preparation = Preparation();
        Dictionary<string, int> reads = new(StringComparer.Ordinal);
        FakeReadOnlyFileSystemProbe probe = new()
        {
            GetAttributesHandler = path =>
            {
                reads.TryGetValue(path, out int count);
                reads[path] = count + 1;
                return count == 0 ? FileAttributes.Normal : FileAttributes.Directory;
            },
        };
        PhysicalDestinationStateInspector inspector = new(probe);

        DestinationStateSnapshot snapshot = inspector.Inspect(
            preparation,
            TestContext.Current.CancellationToken);

        Assert.All(
            snapshot.Facts,
            fact =>
            {
                Assert.Equal(DestinationEntryKind.InspectionFailed, fact.EntryKind);
                Assert.Equal(
                    DestinationInspectionFailureKind.EntryChanged,
                    Assert.IsType<DestinationInspectionFailure>(fact.Failure).Kind);
            });
    }

    [Fact]
    public void DisappearanceAfterTheFirstReadIsEntryChangedRatherThanMissing()
    {
        OrganizationPlanPreparation preparation = Preparation();
        HashSet<string> observed = new(StringComparer.Ordinal);
        FakeReadOnlyFileSystemProbe probe = new()
        {
            GetAttributesHandler = path => observed.Add(path)
                ? FileAttributes.Normal
                : throw new FileNotFoundException(),
        };
        PhysicalDestinationStateInspector inspector = new(probe);

        DestinationStateSnapshot snapshot = inspector.Inspect(
            preparation,
            TestContext.Current.CancellationToken);

        Assert.All(
            snapshot.Facts,
            fact => Assert.Equal(
                DestinationInspectionFailureKind.EntryChanged,
                Assert.IsType<DestinationInspectionFailure>(fact.Failure).Kind));
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), DestinationInspectionFailureKind.AccessDenied)]
    [InlineData(typeof(ArgumentException), DestinationInspectionFailureKind.InvalidPath)]
    [InlineData(typeof(NotSupportedException), DestinationInspectionFailureKind.InvalidPath)]
    [InlineData(typeof(IOException), DestinationInspectionFailureKind.InputOutputFailure)]
    public void MapsInspectionFailuresToExactStructuredKinds(
        Type exceptionType,
        DestinationInspectionFailureKind expectedKind)
    {
        OrganizationPlanPreparation preparation = Preparation();
        FakeReadOnlyFileSystemProbe probe = new()
        {
            GetAttributesHandler = _ => throw CreateException(exceptionType),
        };
        PhysicalDestinationStateInspector inspector = new(probe);

        DestinationStateSnapshot snapshot = inspector.Inspect(
            preparation,
            TestContext.Current.CancellationToken);

        Assert.All(
            snapshot.Facts,
            fact => Assert.Equal(
                expectedKind,
                Assert.IsType<DestinationInspectionFailure>(fact.Failure).Kind));
    }

    [Fact]
    public void IncompatiblePathSyntaxProducesInvalidPathFactsWithoutPhysicalReads()
    {
        PathPolicy incompatible = new(
            OperatingSystem.IsWindows() ? PathSyntax.Posix : PathSyntax.Windows,
            PathCaseSensitivity.Sensitive,
            PathUnicodeNormalization.None);
        string sourceRoot = OperatingSystem.IsWindows() ? "/inbox" : @"C:\Inbox";
        string destination = OperatingSystem.IsWindows() ? "/archive" : @"C:\Archive";
        OrganizationPlanPreparation preparation = DestinationInspectorTestPreparation.Create(
            incompatible,
            sourceRoot,
            destination);
        FakeReadOnlyFileSystemProbe probe = new()
        {
            GetAttributesHandler = _ => throw new InvalidOperationException(
                "An incompatible path must not reach the physical probe."),
        };
        PhysicalDestinationStateInspector inspector = new(probe);

        DestinationStateSnapshot snapshot = inspector.Inspect(
            preparation,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(snapshot.Facts);
        Assert.All(
            snapshot.Facts,
            fact => Assert.Equal(
                DestinationInspectionFailureKind.InvalidPath,
                Assert.IsType<DestinationInspectionFailure>(fact.Failure).Kind));
        Assert.Equal(0, probe.AttributeReadCount);
    }

    [Fact]
    public void CancellationWinsOverAConcurrentIoFailure()
    {
        OrganizationPlanPreparation preparation = Preparation();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        FakeReadOnlyFileSystemProbe probe = new()
        {
            GetAttributesHandler = _ =>
            {
                cancellation.Cancel();
                throw new IOException("simulated concurrent failure");
            },
        };
        PhysicalDestinationStateInspector inspector = new(probe);

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(
            () => inspector.Inspect(preparation, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private OrganizationPlanPreparation Preparation()
    {
        string basePath = Path.Combine(
            Path.GetTempPath(),
            "FileOrganizer.Tests",
            "fake-destination-inspection");

        return DestinationInspectorTestPreparation.Create(
            _paths,
            Path.Combine(basePath, "inbox"),
            Path.Combine(basePath, "archive"));
    }

    private static DestinationPathFact Fact(
        DestinationStateSnapshot snapshot,
        CanonicalPath path) =>
        Assert.Single(snapshot.Facts, fact => fact.Path.Value == path.Value);

    private static Exception CreateException(Type exceptionType) =>
        (Exception)Activator.CreateInstance(exceptionType)!;

    private sealed class FakeReadOnlyFileSystemProbe : IReadOnlyFileSystemProbe
    {
        public Func<string, FileAttributes> GetAttributesHandler { get; set; } =
            _ => throw new InvalidOperationException("No attribute read was configured.");

        public int AttributeReadCount { get; private set; }

        public FileAttributes GetAttributes(string path)
        {
            AttributeReadCount++;
            return GetAttributesHandler(path);
        }

        public IEnumerable<string> EnumerateEntries(string directoryPath) =>
            throw new InvalidOperationException("Destination inspection must not enumerate.");

        public ReadOnlyFileSnapshot ReadFileSnapshot(string path) =>
            throw new InvalidOperationException("Destination inspection must not read metadata.");
    }
}
