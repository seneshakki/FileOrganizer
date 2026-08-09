using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;
using FileOrganizer.Infrastructure.FileSystem;

namespace FileOrganizer.Infrastructure.Tests.FileSystem;

public sealed class PhysicalDestinationStateInspectorFailureTests
{
    private readonly PathPolicy _paths = DestinationInspectorTestPreparation.HostPathPolicy();

    [Fact]
    public void ClassifiesFilesAndDirectoriesWithMatchingStrongIdentity()
    {
        OrganizationPlanPreparation preparation = Preparation();
        CanonicalPath directory = Assert.Single(preparation.DestinationDirectories);
        CanonicalPath target = Assert.Single(preparation.ProposedTargetPaths);
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = path => path.Value == directory.Value
                ? DirectoryObservation("1")
                : FileObservation("2"),
        };
        PhysicalDestinationStateInspector inspector = new(probe);

        DestinationStateSnapshot snapshot = inspector.Inspect(
            preparation,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshot.Facts.Count);
        DestinationPathFact directoryFact = Fact(snapshot, directory);
        DestinationPathFact targetFact = Fact(snapshot, target);
        Assert.Equal(DestinationEntryKind.Directory, directoryFact.EntryKind);
        Assert.Equal(PhysicalEntryKind.Directory, directoryFact.PhysicalIdentity?.EntryKind);
        Assert.Equal(DestinationEntryKind.File, targetFact.EntryKind);
        Assert.Equal(PhysicalEntryKind.RegularFile, targetFact.PhysicalIdentity?.EntryKind);
        Assert.All(snapshot.Facts, fact => Assert.Null(fact.Failure));
        Assert.Equal(2, probe.ObservationCount);
    }

    [Fact]
    public void MissingPathsAreExplicitFactsAndAreObservedOnce()
    {
        OrganizationPlanPreparation preparation = Preparation();
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = _ => PhysicalEntryObservation.Missing(),
        };
        PhysicalDestinationStateInspector inspector = new(probe);

        DestinationStateSnapshot snapshot = inspector.Inspect(
            preparation,
            TestContext.Current.CancellationToken);

        Assert.All(
            snapshot.Facts,
            fact => Assert.Equal(DestinationEntryKind.Missing, fact.EntryKind));
        Assert.All(snapshot.Facts, fact => Assert.Null(fact.Failure));
        Assert.All(snapshot.Facts, fact => Assert.Null(fact.PhysicalIdentity));
        Assert.Equal(preparation.DestinationPaths.Count, probe.ObservationCount);
    }

    [Theory]
    [InlineData(PhysicalEntryObservationReason.ReparsePoint, PhysicalEntryKind.ReparsePoint)]
    [InlineData(PhysicalEntryObservationReason.UnsupportedEntryKind, PhysicalEntryKind.Unsupported)]
    public void UnsupportedEntryTypesFailClosed(
        PhysicalEntryObservationReason reason,
        PhysicalEntryKind kind)
    {
        OrganizationPlanPreparation preparation = Preparation();
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = _ => PhysicalEntryObservation.Unsupported(reason, kind),
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
                Assert.Null(fact.PhysicalIdentity);
            });
    }

    [Fact]
    public void ExplicitEntryChangedObservationFailsClosed()
    {
        OrganizationPlanPreparation preparation = Preparation();
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = _ => PhysicalEntryObservation.Inaccessible(
                PhysicalEntryObservationReason.EntryChanged),
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

    [Fact]
    public void IdentityUnsupportedDirectoryRemainsAPreviewFactWithoutIdentity()
    {
        OrganizationPlanPreparation preparation = Preparation();
        CanonicalPath directory = Assert.Single(preparation.DestinationDirectories);
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = path => path.Value == directory.Value
                ? PhysicalEntryObservation.Unsupported(
                    PhysicalEntryObservationReason.UnsupportedCapability,
                    PhysicalEntryKind.Directory)
                : PhysicalEntryObservation.Missing(),
        };
        PhysicalDestinationStateInspector inspector = new(probe);

        DestinationStateSnapshot snapshot = inspector.Inspect(
            preparation,
            TestContext.Current.CancellationToken);

        DestinationPathFact fact = Fact(snapshot, directory);
        Assert.Equal(DestinationEntryKind.Directory, fact.EntryKind);
        Assert.Null(fact.Failure);
        Assert.Null(fact.PhysicalIdentity);
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), DestinationInspectionFailureKind.AccessDenied)]
    [InlineData(typeof(ArgumentException), DestinationInspectionFailureKind.InvalidPath)]
    [InlineData(typeof(NotSupportedException), DestinationInspectionFailureKind.InvalidPath)]
    [InlineData(typeof(IOException), DestinationInspectionFailureKind.InputOutputFailure)]
    public void MapsInspectionExceptionsToExactStructuredKinds(
        Type exceptionType,
        DestinationInspectionFailureKind expectedKind)
    {
        OrganizationPlanPreparation preparation = Preparation();
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = _ => throw CreateException(exceptionType),
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

    [Theory]
    [InlineData(PhysicalEntryObservationReason.AccessDenied, DestinationInspectionFailureKind.AccessDenied)]
    [InlineData(PhysicalEntryObservationReason.SharingViolation, DestinationInspectionFailureKind.InputOutputFailure)]
    [InlineData(PhysicalEntryObservationReason.InputOutputFailure, DestinationInspectionFailureKind.InputOutputFailure)]
    public void MapsInaccessibleObservationsToExactStructuredKinds(
        PhysicalEntryObservationReason reason,
        DestinationInspectionFailureKind expectedKind)
    {
        OrganizationPlanPreparation preparation = Preparation();
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = _ => PhysicalEntryObservation.Inaccessible(reason),
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
            ObserveHandler = _ => throw new InvalidOperationException(
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
        Assert.Equal(0, probe.ObservationCount);
    }

    [Fact]
    public void CancellationWinsOverAConcurrentIoFailure()
    {
        OrganizationPlanPreparation preparation = Preparation();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = _ =>
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

    private static PhysicalEntryObservation DirectoryObservation(string identifier) =>
        PhysicalEntryObservation.Success(
            Identity(identifier, PhysicalEntryKind.Directory));

    private static PhysicalEntryObservation FileObservation(string identifier) =>
        PhysicalEntryObservation.Success(
            Identity(identifier, PhysicalEntryKind.RegularFile),
            new PhysicalFileMetadata(
                10,
                null,
                new DateTimeOffset(2026, 4, 5, 6, 7, 8, TimeSpan.Zero)));

    private static PhysicalEntryIdentity Identity(
        string identifier,
        PhysicalEntryKind kind) =>
        new(
            PhysicalEntryIdentity.WindowsFileIdScheme,
            PhysicalEntryIdentity.WindowsFileIdSchemeVersion,
            "0000000000000001",
            identifier.PadLeft(32, '0'),
            kind);

    private sealed class FakeReadOnlyFileSystemProbe : IReadOnlyFileSystemProbe
    {
        public Func<CanonicalPath, PhysicalEntryObservation> ObserveHandler { get; set; } =
            _ => throw new InvalidOperationException("No physical observation was configured.");

        public int ObservationCount { get; private set; }

        public PhysicalEntryObservation Observe(
            CanonicalPath path,
            CancellationToken cancellationToken = default)
        {
            ObservationCount++;
            return ObserveHandler(path);
        }

        public IEnumerable<string> EnumerateEntries(string directoryPath) =>
            throw new InvalidOperationException("Destination inspection must not enumerate.");
    }
}
