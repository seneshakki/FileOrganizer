using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Scanning;
using FileOrganizer.Infrastructure.FileSystem;

namespace FileOrganizer.Infrastructure.Tests.FileSystem;

/// <summary>
/// These tests touch only unique directories created beneath the system temp
/// path. Production scanner code remains read-only.
/// </summary>
public sealed class PhysicalFileScannerTests
{
    private readonly PathPolicy _paths = HostPathPolicy();

    public PhysicalFileScannerTests() =>
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "The M3A physical adapter is intentionally Windows-only.");

    [Fact]
    public void ReturnsFilesAndCanonicalRootWithoutChangingTheDirectory()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string first = temp.WriteFile("a.txt", "one");
        string second = temp.WriteFile("b.pdf", "12345");
        PhysicalFileScanner scanner = new(_paths);

        FileScanResult result = scanner.Scan(
            Canonical(temp.FullPath),
            recursive: false,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsComplete);
        Assert.Equal(Canonical(temp.FullPath).Value, result.CanonicalRoot.Value);
        Assert.Equal(["a.txt", "b.pdf"], result.Files.Select(file => file.Name));
        Assert.Equal([3, 5], result.Files.Select(file => file.SizeInBytes));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void HonorsTheRequestedRecursionScope()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        temp.WriteFile("top.txt");
        temp.WriteFile(Path.Combine("nested", "deeper", "deep.txt"));
        PhysicalFileScanner scanner = new(_paths);

        FileScanResult shallow = scanner.Scan(
            Canonical(temp.FullPath),
            recursive: false,
            TestContext.Current.CancellationToken);
        FileScanResult recursive = scanner.Scan(
            Canonical(temp.FullPath),
            recursive: true,
            TestContext.Current.CancellationToken);

        Assert.True(shallow.IsComplete);
        Assert.Equal(["top.txt"], shallow.Files.Select(file => file.Name));
        Assert.True(recursive.IsComplete);
        Assert.Equal(["deep.txt", "top.txt"], recursive.Files.Select(file => file.Name));
    }

    [Fact]
    public void ReadsUtcMetadata()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        temp.WriteFile("report.pdf", "12345");
        PhysicalFileScanner scanner = new(_paths);

        FileScanResult result = scanner.Scan(
            Canonical(temp.FullPath),
            recursive: false,
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.Files);
        Assert.Equal(5, entry.SizeInBytes);
        Assert.Equal(TimeSpan.Zero, entry.ModifiedUtc.Offset);
        if (OperatingSystem.IsLinux())
        {
            Assert.Null(entry.CreatedUtc);
        }
        else
        {
            Assert.NotNull(entry.CreatedUtc);
            Assert.Equal(TimeSpan.Zero, entry.CreatedUtc.Value.Offset);
        }
    }

    [Fact]
    public void MissingRootProducesAnIncompleteStructuredResult()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        CanonicalPath missing = Canonical(Path.Combine(temp.FullPath, "absent"));
        PhysicalFileScanner scanner = new(_paths);

        FileScanResult result = scanner.Scan(
            missing,
            recursive: true,
            TestContext.Current.CancellationToken);

        ScanDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.False(result.IsComplete);
        Assert.Empty(result.Files);
        Assert.Equal(ScanDiagnosticKind.RootNotFound, diagnostic.Kind);
        Assert.Equal(missing.Value, diagnostic.AffectedPath);
    }

    [Fact]
    public void FileUsedAsRootProducesAnIncompleteStructuredResult()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        CanonicalPath file = Canonical(temp.WriteFile("not-a-directory.txt"));
        PhysicalFileScanner scanner = new(_paths);

        FileScanResult result = scanner.Scan(
            file,
            recursive: true,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
        Assert.Equal(
            ScanDiagnosticKind.RootIsNotDirectory,
            Assert.Single(result.Diagnostics).Kind);
    }

    [Fact]
    public void DoesNotSilentlySkipAHiddenRegularFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Hidden attributes differ by platform.");

        using TemporaryDirectory temp = TemporaryDirectory.Create();
        temp.WriteFile("visible.txt");
        string hidden = temp.WriteFile("hidden.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        PhysicalFileScanner scanner = new(_paths);

        FileScanResult result = scanner.Scan(
            Canonical(temp.FullPath),
            recursive: false,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsComplete);
        Assert.Equal(["hidden.txt", "visible.txt"], result.Files.Select(file => file.Name));
    }

    [Fact]
    public void SkipsAndReportsDirectorySymbolicLinksWithoutFollowingThem()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string target = Path.Combine(temp.FullPath, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "inside.txt"), "content");
        string link = Path.Combine(temp.FullPath, "link");

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic links are unavailable in this test environment: {exception.GetType().Name}");
        }

        PhysicalFileScanner scanner = new(_paths);

        FileScanResult result = scanner.Scan(
            Canonical(temp.FullPath),
            recursive: true,
            TestContext.Current.CancellationToken);

        ScanDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.False(result.IsComplete);
        Assert.Equal(ScanDiagnosticKind.ReparsePointSkipped, diagnostic.Kind);
        Assert.Equal(Canonical(link).Value, diagnostic.AffectedPath);
        Assert.Equal(["inside.txt"], result.Files.Select(file => file.Name));
    }

    [Fact]
    public void PreCanceledScanPropagatesCancellation()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        PhysicalFileScanner scanner = new(_paths);

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(
            () => scanner.Scan(
                Canonical(temp.FullPath),
                recursive: true,
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private CanonicalPath Canonical(string path)
    {
        PathResolution resolution = _paths.CanonicalizeAbsolute(path);
        return Assert.IsType<CanonicalPath>(resolution.Path);
    }

    private static PathPolicy HostPathPolicy() => new(
        OperatingSystem.IsWindows() ? PathSyntax.Windows : PathSyntax.Posix,
        OperatingSystem.IsWindows()
            ? PathCaseSensitivity.Insensitive
            : PathCaseSensitivity.Sensitive,
        PathUnicodeNormalization.None);
}
