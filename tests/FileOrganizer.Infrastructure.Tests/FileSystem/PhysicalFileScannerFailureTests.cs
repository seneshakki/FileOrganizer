using FileOrganizer.Core.Paths;
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
            GetAttributesHandler = _ => throw new UnauthorizedAccessException(),
        };
        PhysicalFileScanner scanner = new(_paths, probe);

        FileScanResult result = scanner.Scan(
            root,
            recursive: true,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Files);
        Assert.Equal(ScanDiagnosticKind.AccessDenied, Assert.Single(result.Diagnostics).Kind);
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
    public void ReportsAnEntryThatDisappearsBeforeAttributeInspection()
    {
        CanonicalPath root = Root();
        string vanished = Child(root, "vanished.txt");
        FakeReadOnlyFileSystemProbe probe = new()
        {
            GetAttributesHandler = path => path == root.Value
                ? FileAttributes.Directory
                : throw new FileNotFoundException(),
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
        probe.ReadFileSnapshotHandler = _ => throw new IOException("simulated failure");
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
            GetAttributesHandler = path => path == root.Value
                ? FileAttributes.Directory
                : FileAttributes.Directory | FileAttributes.ReparsePoint,
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
    public void RejectsAnEntryThatBecomesAReparsePointBesideMetadataCapture()
    {
        CanonicalPath root = Root();
        string changed = Child(root, "changed.txt");
        FakeReadOnlyFileSystemProbe probe = RegularFileProbe(root, changed);
        probe.ReadFileSnapshotHandler = _ =>
            new ReadOnlyFileSnapshot(FileAttributes.ReparsePoint, null);
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
            GetAttributesHandler = path => path == root.Value
                ? FileAttributes.Directory
                : FileAttributes.Directory | FileAttributes.Device,
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
    public void ReportsAFileThatBecomesADirectoryAsChanged()
    {
        CanonicalPath root = Root();
        string changed = Child(root, "changed.txt");
        FakeReadOnlyFileSystemProbe probe = RegularFileProbe(root, changed);
        probe.ReadFileSnapshotHandler = _ =>
            new ReadOnlyFileSnapshot(FileAttributes.Directory, null);
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
    public void RechecksAQueuedDirectoryAndDoesNotFollowItIfItBecomesAReparsePoint()
    {
        CanonicalPath root = Root();
        string childDirectory = Child(root, "changing-directory");
        int childAttributeReads = 0;
        FakeReadOnlyFileSystemProbe probe = new()
        {
            GetAttributesHandler = path =>
            {
                if (path == root.Value)
                {
                    return FileAttributes.Directory;
                }

                childAttributeReads++;
                return childAttributeReads == 1
                    ? FileAttributes.Directory
                    : FileAttributes.Directory | FileAttributes.ReparsePoint;
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
        Assert.Equal(2, childAttributeReads);
        Assert.Equal(
            ScanDiagnosticKind.ReparsePointSkipped,
            Assert.Single(result.Diagnostics).Kind);
    }

    [Fact]
    public void RejectsAReportedEntryOutsideTheSourceRoot()
    {
        CanonicalPath root = Root();
        CanonicalPath parent = Assert.IsType<CanonicalPath>(_paths.GetParent(root));
        string outside = Child(parent, "outside.txt");
        FakeReadOnlyFileSystemProbe probe = new()
        {
            GetAttributesHandler = _ => FileAttributes.Directory,
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
            GetAttributesHandler = _ => FileAttributes.Directory,
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
        Assert.Equal(2, probe.AttributeReadCount);
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
            GetAttributesHandler = _ => FileAttributes.Directory,
            EnumerateEntriesHandler = _ => CancelThenYield(cancellation, file),
        };
        PhysicalFileScanner scanner = new(_paths, probe);

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(
            () => scanner.Scan(root, recursive: true, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(2, probe.AttributeReadCount);
        Assert.Equal(0, probe.MetadataReadCount);
    }

    [Fact]
    public void CancellationWinsOverAConcurrentIoFailure()
    {
        CanonicalPath root = Root();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        FakeReadOnlyFileSystemProbe probe = new()
        {
            GetAttributesHandler = _ =>
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
            GetAttributesHandler = path => path == root.Value
                ? FileAttributes.Directory
                : FileAttributes.Normal,
            EnumerateEntriesHandler = _ => [file],
            ReadFileSnapshotHandler = _ =>
                new ReadOnlyFileSnapshot(
                    FileAttributes.Normal,
                    new ReadOnlyFileMetadata(10, null, Modified)),
        };

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
        public Func<string, FileAttributes> GetAttributesHandler { get; set; } =
            _ => throw new InvalidOperationException("No attribute read was configured.");

        public Func<string, IEnumerable<string>> EnumerateEntriesHandler { get; set; } =
            _ => throw new InvalidOperationException("No enumeration was configured.");

        public Func<string, ReadOnlyFileSnapshot> ReadFileSnapshotHandler { get; set; } =
            _ => throw new InvalidOperationException("No metadata read was configured.");

        public int AttributeReadCount { get; private set; }

        public int MetadataReadCount { get; private set; }

        public FileAttributes GetAttributes(string path)
        {
            AttributeReadCount++;
            return GetAttributesHandler(path);
        }

        public IEnumerable<string> EnumerateEntries(string directoryPath) =>
            EnumerateEntriesHandler(directoryPath);

        public ReadOnlyFileSnapshot ReadFileSnapshot(string path)
        {
            MetadataReadCount++;
            return ReadFileSnapshotHandler(path);
        }
    }
}
