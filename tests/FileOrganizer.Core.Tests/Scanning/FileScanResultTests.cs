using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Core.Tests.Scanning;

public sealed class FileScanResultTests
{
    private static readonly DateTimeOffset Modified =
        new(2026, 4, 5, 6, 7, 8, TimeSpan.Zero);

    private readonly PathPolicy _paths = new(
        PathSyntax.Windows,
        PathCaseSensitivity.Insensitive,
        PathUnicodeNormalization.FormC);

    [Fact]
    public void SnapshotsCallerOwnedCollectionsAndDoesNotExposeMutableLists()
    {
        FileEntry file = Entry(@"C:\Inbox\report.pdf");
        ScanDiagnostic diagnostic = new(
            ScanDiagnosticKind.MetadataReadFailed,
            @"C:\Inbox\unreadable.pdf");
        List<FileEntry> files = [file];
        List<ScanDiagnostic> diagnostics = [diagnostic];

        FileScanResult result = new(Path(@"C:\Inbox"), files, diagnostics);
        files.Clear();
        diagnostics.Clear();

        Assert.Same(file, Assert.Single(result.Files));
        Assert.Same(diagnostic, Assert.Single(result.Diagnostics));
        Assert.Throws<NotSupportedException>(
            () => ((IList<FileEntry>)result.Files)[0] = Entry(@"C:\Inbox\other.pdf"));
        Assert.Throws<NotSupportedException>(
            () => ((IList<ScanDiagnostic>)result.Diagnostics)[0] =
                new ScanDiagnostic(ScanDiagnosticKind.AccessDenied, @"C:\Inbox"));
    }

    [Fact]
    public void AScanIsCompleteOnlyWhenItHasNoDiagnostics()
    {
        FileScanResult complete = new(Path(@"C:\Inbox"), [], []);
        FileScanResult incomplete = new(
            Path(@"C:\Inbox"),
            [],
            [new ScanDiagnostic(ScanDiagnosticKind.ReparsePointSkipped, @"C:\Inbox\link")]);

        Assert.True(complete.IsComplete);
        Assert.False(incomplete.IsComplete);
    }

    [Fact]
    public void SortsFilesAndDiagnosticsDeterministically()
    {
        FileEntry lowercase = Entry(@"C:\Inbox\alpha.txt");
        FileEntry uppercase = Entry(@"C:\Inbox\ALPHA.txt");
        FileEntry last = Entry(@"C:\Inbox\zeta.txt");
        ScanDiagnostic later = new(ScanDiagnosticKind.AccessDenied, @"C:\Inbox\zeta.txt");
        ScanDiagnostic earlier = new(
            ScanDiagnosticKind.MetadataReadFailed,
            @"C:\Inbox\alpha.txt");

        FileScanResult result = new(
            Path(@"C:\Inbox"),
            [last, lowercase, uppercase],
            [later, earlier]);

        Assert.Equal(
            [@"C:\Inbox\ALPHA.txt", @"C:\Inbox\alpha.txt", @"C:\Inbox\zeta.txt"],
            result.Files.Select(file => file.FullPath));
        Assert.Equal(
            [@"C:\Inbox\alpha.txt", @"C:\Inbox\zeta.txt"],
            result.Diagnostics.Select(diagnostic => diagnostic.AffectedPath));
    }

    [Fact]
    public void PhysicalIdentityBreaksOtherwiseEqualSourceOrderingDeterministically()
    {
        CanonicalPath path = Path(@"C:\Inbox\report.pdf");
        PhysicalEntryIdentity firstIdentity = TestFiles.FileIdentity("entry-2", "volume-1");
        PhysicalEntryIdentity secondIdentity = TestFiles.FileIdentity("entry-1", "volume-2");
        FileEntry first = new(path, 1, null, Modified, firstIdentity);
        FileEntry second = new(path, 1, null, Modified, secondIdentity);

        FileScanResult forward = new(Path(@"C:\Inbox"), [first, second], []);
        FileScanResult reversed = new(Path(@"C:\Inbox"), [second, first], []);

        string[] expected = ["volume-1/entry-2", "volume-2/entry-1"];
        Assert.Equal(expected, forward.Files.Select(IdentityKey));
        Assert.Equal(expected, reversed.Files.Select(IdentityKey));
    }

    [Fact]
    public void PropagatesCancellationDuringImmutableMaterialization()
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(
            () => new FileScanResult(
                Path(@"C:\Inbox"),
                CancelBeforeSecondFile(cancellation),
                [],
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public void RetainsOnlyADirectoryIdentityForTheScanRoot()
    {
        PhysicalEntryIdentity rootIdentity = TestFiles.DirectoryIdentity("root-1");

        FileScanResult result = new(Path(@"C:\Inbox"), [], [], rootIdentity);

        Assert.Same(rootIdentity, result.RootPhysicalIdentity);
        Assert.Throws<ArgumentException>(
            () => new FileScanResult(
                Path(@"C:\Inbox"),
                [],
                [],
                TestFiles.FileIdentity("file-1")));
    }

    private FileEntry Entry(string path) => new(Path(path), 1, null, Modified);

    private static string IdentityKey(FileEntry entry) =>
        $"{entry.PhysicalIdentity!.VolumeIdentity}/{entry.PhysicalIdentity.EntryIdentifier}";

    private IEnumerable<FileEntry> CancelBeforeSecondFile(
        CancellationTokenSource cancellation)
    {
        yield return Entry(@"C:\Inbox\first.txt");
        cancellation.Cancel();
        yield return Entry(@"C:\Inbox\second.txt");
    }

    private CanonicalPath Path(string value)
    {
        PathResolution resolution = _paths.CanonicalizeAbsolute(value);
        return Assert.IsType<CanonicalPath>(resolution.Path);
    }
}
