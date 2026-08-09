using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Scanning;
using FileOrganizer.Infrastructure.FileSystem;

namespace FileOrganizer.Infrastructure.Tests.FileSystem;

public sealed class PhysicalFileScannerFailureTests
{
    private static readonly DateTimeOffset Modified =
        new(2026, 4, 5, 6, 7, 8, TimeSpan.Zero);

    private readonly PathPolicy _paths = HostPathPolicy();

    [Fact]
    public void AccessDeniedIsNotDisguisedAsAnEmptyCompleteScan()
    {
        CanonicalPath root = Root();
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = _ => throw new UnauthorizedAccessException(),
        };
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: true,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Files);
        Assert.Equal(ScanDiagnosticKind.AccessDenied, Assert.Single(result.Diagnostics).Kind);
        Assert.Null(result.RootPhysicalIdentity);
    }

    [Fact]
    public void KeepsDiscoveredFilesButReportsALazyEnumerationFailure()
    {
        CanonicalPath root = Root();
        string file = Child(root, "found.txt");
        FakeReadOnlyFileSystemProbe probe = RegularFileProbe(root, file);
        probe.EnumerateEntriesHandler = _ => YieldThenThrow(file);
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.Equal("found.txt", Assert.Single(result.Files).Name);
        Assert.Equal(
            ScanDiagnosticKind.DirectoryEnumerationFailed,
            Assert.Single(result.Diagnostics).Kind);
    }

    [Fact]
    public void ReportsAnEntryThatDisappearsBeforeHandleObservation()
    {
        CanonicalPath root = Root();
        string vanished = Child(root, "vanished.txt");
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = path => path.Value == root.Value
                ? DirectoryObservation("1")
                : PhysicalEntryObservation.Missing(),
            EnumerateEntriesHandler = _ => [vanished],
        };
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Files);
        Assert.Equal(
            ScanDiagnosticKind.EntryDisappeared,
            Assert.Single(result.Diagnostics).Kind);
    }

    [Fact]
    public void ReportsMetadataReadFailurePrecisely()
    {
        CanonicalPath root = Root();
        string file = Child(root, "unreadable.txt");
        FakeReadOnlyFileSystemProbe probe = RegularFileProbe(root, file);
        probe.ObserveHandler = path => path.Value == root.Value
            ? DirectoryObservation("1")
            : PhysicalEntryObservation.Inaccessible(
                PhysicalEntryObservationReason.InputOutputFailure);
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Files);
        Assert.Equal(
            ScanDiagnosticKind.MetadataReadFailed,
            Assert.Single(result.Diagnostics).Kind);
    }

    [Fact]
    public void ReportsAndNeverTraversesAReparsePoint()
    {
        CanonicalPath root = Root();
        string link = Child(root, "link");
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = path => path.Value == root.Value
                ? DirectoryObservation("1")
                : PhysicalEntryObservation.Unsupported(
                    PhysicalEntryObservationReason.ReparsePoint,
                    PhysicalEntryKind.ReparsePoint),
            EnumerateEntriesHandler = path => path == root.Value
                ? [link]
                : throw new InvalidOperationException("The link must not be followed."),
        };
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: true,
            TestContext.Current.CancellationToken);

        ScanDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.False(result.IsComplete);
        Assert.Empty(result.Files);
        Assert.Equal(ScanDiagnosticKind.ReparsePointSkipped, diagnostic.Kind);
        Assert.Equal(link, diagnostic.AffectedPath);
    }

    [Fact]
    public void RejectsAnEntryObservedAsAReparsePointBesideMetadataCapture()
    {
        CanonicalPath root = Root();
        string changed = Child(root, "changed.txt");
        FakeReadOnlyFileSystemProbe probe = RegularFileProbe(root, changed);
        probe.ObserveHandler = path => path.Value == root.Value
            ? DirectoryObservation("1")
            : PhysicalEntryObservation.Unsupported(
                PhysicalEntryObservationReason.ReparsePoint,
                PhysicalEntryKind.ReparsePoint);
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Files);
        Assert.Equal(
            ScanDiagnosticKind.ReparsePointSkipped,
            Assert.Single(result.Diagnostics).Kind);
    }

    [Fact]
    public void ReportsAnUnsupportedDirectoryEvenWhenRecursionIsDisabled()
    {
        CanonicalPath root = Root();
        string unsupported = Child(root, "device-directory");
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = path => path.Value == root.Value
                ? DirectoryObservation("1")
                : PhysicalEntryObservation.Unsupported(
                    PhysicalEntryObservationReason.UnsupportedEntryKind,
                    PhysicalEntryKind.Unsupported),
            EnumerateEntriesHandler = _ => [unsupported],
        };
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Files);
        Assert.Equal(
            ScanDiagnosticKind.UnsupportedEntrySkipped,
            Assert.Single(result.Diagnostics).Kind);
    }

    [Fact]
    public void ReportsAnEntryKnownToHaveChangedType()
    {
        CanonicalPath root = Root();
        string changed = Child(root, "changed.txt");
        FakeReadOnlyFileSystemProbe probe = RegularFileProbe(root, changed);
        probe.ObserveHandler = path => path.Value == root.Value
            ? DirectoryObservation("1")
            : PhysicalEntryObservation.Inaccessible(
                PhysicalEntryObservationReason.EntryChanged,
                entryKind: PhysicalEntryKind.Directory);
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Files);
        Assert.Equal(
            ScanDiagnosticKind.EntryChanged,
            Assert.Single(result.Diagnostics).Kind);
    }

    [Fact]
    public void ReportsARootRaceAsEntryChangedRatherThanAStaticWrongKind()
    {
        CanonicalPath root = Root();
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = _ => PhysicalEntryObservation.Inaccessible(
                PhysicalEntryObservationReason.EntryChanged,
                entryKind: PhysicalEntryKind.RegularFile),
        };
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Files);
        Assert.Equal(
            ScanDiagnosticKind.EntryChanged,
            Assert.Single(result.Diagnostics).Kind);
        Assert.Null(result.RootPhysicalIdentity);
    }

    [Fact]
    public void RechecksAQueuedDirectoryAndDoesNotFollowItIfItBecomesAReparsePoint()
    {
        CanonicalPath root = Root();
        string childDirectory = Child(root, "changing-directory");
        int childObservations = 0;
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = path =>
            {
                if (path.Value == root.Value)
                {
                    return DirectoryObservation("1");
                }

                childObservations++;
                return childObservations == 1
                    ? DirectoryObservation("2")
                    : PhysicalEntryObservation.Unsupported(
                        PhysicalEntryObservationReason.ReparsePoint,
                        PhysicalEntryKind.ReparsePoint);
            },
            EnumerateEntriesHandler = path => path == root.Value
                ? [childDirectory]
                : throw new InvalidOperationException(
                    "A directory that became a reparse point must not be followed."),
        };
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: true,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Files);
        Assert.Equal(2, childObservations);
        Assert.Equal(
            ScanDiagnosticKind.ReparsePointSkipped,
            Assert.Single(result.Diagnostics).Kind);
    }

    [Fact]
    public void IdentityUnsupportedFileMetadataRemainsAvailableForPreview()
    {
        CanonicalPath root = Root();
        string file = Child(root, "preview.txt");
        PhysicalFileMetadata metadata = Metadata();
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = path => path.Value == root.Value
                ? PhysicalEntryObservation.Unsupported(
                    PhysicalEntryObservationReason.UnsupportedCapability,
                    PhysicalEntryKind.Directory)
                : PhysicalEntryObservation.Unsupported(
                    PhysicalEntryObservationReason.UnsupportedCapability,
                    PhysicalEntryKind.RegularFile,
                    metadata),
            EnumerateEntriesHandler = _ => [file],
        };
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: false,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsComplete);
        Assert.Null(result.RootPhysicalIdentity);
        var entry = Assert.Single(result.Files);
        Assert.Null(entry.PhysicalIdentity);
        Assert.Equal(metadata.Length, entry.SizeInBytes);
        Assert.Equal(metadata.ModifiedUtc, entry.ModifiedUtc);
    }

    [Fact]
    public void RejectsAReportedEntryOutsideTheSourceRoot()
    {
        CanonicalPath root = Root();
        CanonicalPath parent = Assert.IsType<CanonicalPath>(_paths.GetParent(root));
        string outside = Child(parent, "outside.txt");
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = _ => DirectoryObservation("1"),
            EnumerateEntriesHandler = _ => [outside],
        };
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: true,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Files);
        Assert.Equal(
            ScanDiagnosticKind.EntryOutsideRoot,
            Assert.Single(result.Diagnostics).Kind);
    }

    [Fact]
    public void RejectsARelativePathReportedByThePhysicalEnumerator()
    {
        CanonicalPath root = Root();
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = _ => DirectoryObservation("1"),
            EnumerateEntriesHandler = _ => ["relative.txt"],
        };
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Files);
        Assert.Equal(
            ScanDiagnosticKind.InvalidEntryPath,
            Assert.Single(result.Diagnostics).Kind);
        Assert.Equal(2, probe.ObservationCount);
    }

    [Fact]
    public void CancellationDuringLazyEnumerationPropagates()
    {
        CanonicalPath root = Root();
        string file = Child(root, "never-inspected.txt");
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = _ => DirectoryObservation("1"),
            EnumerateEntriesHandler = _ => CancelThenYield(cancellation, file),
        };
        PhysicalFileScanner scanner = new(_paths, probe);

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(
            () => scanner.Scan(root, recursive: true, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(2, probe.ObservationCount);
    }

    [Fact]
    public void CancellationWinsOverAConcurrentIoFailure()
    {
        CanonicalPath root = Root();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        FakeReadOnlyFileSystemProbe probe = new()
        {
            ObserveHandler = _ =>
            {
                cancellation.Cancel();
                throw new IOException("simulated failure after cancellation");
            },
        };
        PhysicalFileScanner scanner = new(_paths, probe);

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(
            () => scanner.Scan(root, recursive: true, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private static FakeReadOnlyFileSystemProbe RegularFileProbe(
        CanonicalPath root,
        string file) =>
        new()
        {
            ObserveHandler = path => path.Value == root.Value
                ? DirectoryObservation("1")
                : FileObservation("2"),
            EnumerateEntriesHandler = _ => [file],
        };

    private static PhysicalEntryObservation DirectoryObservation(string identifier) =>
        PhysicalEntryObservation.Success(
            Identity(identifier, PhysicalEntryKind.Directory));

    private static PhysicalEntryObservation FileObservation(string identifier) =>
        PhysicalEntryObservation.Success(
            Identity(identifier, PhysicalEntryKind.RegularFile),
            Metadata());

    private static PhysicalEntryIdentity Identity(
        string identifier,
        PhysicalEntryKind kind) =>
        new(
            PhysicalEntryIdentity.WindowsFileIdScheme,
            PhysicalEntryIdentity.WindowsFileIdSchemeVersion,
            "0000000000000001",
            identifier.PadLeft(32, '0'),
            kind);

    private static PhysicalFileMetadata Metadata() => new(10, null, Modified);

    private CanonicalPath Root()
    {
        string root = Path.Combine(Path.GetTempPath(), "FileOrganizer.Tests", "fake-scan-root");
        PathResolution resolution = _paths.CanonicalizeAbsolute(root);
        return Assert.IsType<CanonicalPath>(resolution.Path);
    }

    private string Child(CanonicalPath directory, string name)
    {
        PathResolution resolution = _paths.AppendFileName(directory, name);
        return Assert.IsType<CanonicalPath>(resolution.Path).Value;
    }

    private static IEnumerable<string> YieldThenThrow(string file)
    {
        yield return file;
        throw new IOException("simulated lazy enumeration failure");
    }

    private static IEnumerable<string> CancelThenYield(
        CancellationTokenSource cancellation,
        string file)
    {
        cancellation.Cancel();
        yield return file;
    }

    private static PathPolicy HostPathPolicy() => new(
        OperatingSystem.IsWindows() ? PathSyntax.Windows : PathSyntax.Posix,
        OperatingSystem.IsWindows()
            ? PathCaseSensitivity.Insensitive
            : PathCaseSensitivity.Sensitive,
        PathUnicodeNormalization.None);

    private sealed class FakeReadOnlyFileSystemProbe : IReadOnlyFileSystemProbe
    {
        public Func<CanonicalPath, PhysicalEntryObservation> ObserveHandler { get; set; } =
            _ => throw new InvalidOperationException("No physical observation was configured.");

        public Func<string, IEnumerable<string>> EnumerateEntriesHandler { get; set; } =
            _ => throw new InvalidOperationException("No enumeration was configured.");

        public int ObservationCount { get; private set; }

        public PhysicalEntryObservation Observe(
            CanonicalPath path,
            CancellationToken cancellationToken = default)
        {
            ObservationCount++;
            return ObserveHandler(path);
        }

        public IEnumerable<string> EnumerateEntries(string directoryPath) =>
            EnumerateEntriesHandler(directoryPath);
    }
}
