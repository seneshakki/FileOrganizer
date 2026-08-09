using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Core.Tests.Files;

public sealed class FileEntryTests
{
    private static readonly DateTimeOffset SampleInstant =
        new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("report.pdf", "report", ".pdf")]
    [InlineData("archive.tar.gz", "archive.tar", ".gz")]
    [InlineData("notes.v2.final.md", "notes.v2.final", ".md")]
    [InlineData("README", "README", "")]
    public void SplitsNameIntoStemAndExtension(
        string name,
        string expectedStem,
        string expectedExtension)
    {
        FileEntry entry = CreateEntry(name: name);

        Assert.Equal(expectedStem, entry.NameWithoutExtension);
        Assert.Equal(expectedExtension, entry.Extension);
    }

    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".env")]
    [InlineData(".editorconfig")]
    public void TreatsDotfileAsHavingNoExtension(string name)
    {
        FileEntry entry = CreateEntry(name: name);

        Assert.Equal(string.Empty, entry.Extension);
        Assert.Equal(name, entry.NameWithoutExtension);
    }

    [Fact]
    public void TreatsTrailingDotAsHavingNoExtensionUnderPosixPolicy()
    {
        FileEntry entry = CreateEntry(name: "report.");

        Assert.Equal(string.Empty, entry.Extension);
        Assert.Equal("report.", entry.NameWithoutExtension);
    }

    [Fact]
    public void SplitsOnTheLastDotOfADottedStem()
    {
        FileEntry entry = CreateEntry(name: ".config.json");

        Assert.Equal(".config", entry.NameWithoutExtension);
        Assert.Equal(".json", entry.Extension);
    }

    [Fact]
    public void PreservesTheCaseOfTheExtension()
    {
        FileEntry entry = CreateEntry(name: "INVOICE.PDF");

        Assert.Equal("INVOICE", entry.NameWithoutExtension);
        Assert.Equal(".PDF", entry.Extension);
    }

    [Fact]
    public void DerivesNameFromCanonicalPath()
    {
        CanonicalPath path = TestFiles.CanonicalPathFor("quarterly/report.pdf");
        FileEntry entry = CreateEntry(path: path);

        Assert.Same(path, entry.Path);
        Assert.Equal("/test-inbox/quarterly/report.pdf", entry.FullPath);
        Assert.Equal("report.pdf", entry.Name);
    }

    [Fact]
    public void NormalizesTimestampsToUtc()
    {
        DateTimeOffset createdInLocalOffset =
            new(2026, 3, 14, 9, 30, 0, TimeSpan.FromHours(3));

        FileEntry entry = CreateEntry(
            created: createdInLocalOffset,
            modified: createdInLocalOffset);

        Assert.NotNull(entry.CreatedUtc);
        DateTimeOffset createdUtc = entry.CreatedUtc.Value;

        Assert.Equal(TimeSpan.Zero, createdUtc.Offset);
        Assert.Equal(TimeSpan.Zero, entry.ModifiedUtc.Offset);
        Assert.Equal(6, createdUtc.Hour);
        Assert.Equal(createdInLocalOffset, createdUtc);
    }

    [Fact]
    public void KeepsAnUnreportedCreationTimeAsUnknown()
    {
        FileEntry entry = new(
            TestFiles.CanonicalPathFor("report.pdf"),
            1024,
            null,
            SampleInstant);

        Assert.Null(entry.CreatedUtc);
    }

    [Fact]
    public void KeepsTheMetadataItWasGiven()
    {
        FileEntry entry = CreateEntry(sizeInBytes: 4096);

        Assert.Equal(4096, entry.SizeInBytes);
        Assert.Equal(SampleInstant, entry.CreatedUtc);
        Assert.Equal(SampleInstant, entry.ModifiedUtc);
    }

    [Fact]
    public void AcceptsAnEmptyFile()
    {
        FileEntry entry = CreateEntry(sizeInBytes: 0);

        Assert.Equal(0, entry.SizeInBytes);
    }

    [Fact]
    public void RejectsAPathWithNoFileName()
    {
        PathResolution root = TestFiles.Paths.CanonicalizeAbsolute("/");

        Assert.Throws<ArgumentException>(
            () => new FileEntry(root.Path!, 0, null, SampleInstant));
    }

    [Fact]
    public void RejectsNullPath()
    {
        Assert.Throws<ArgumentNullException>(
            () => new FileEntry(null!, 1024, SampleInstant, SampleInstant));
    }

    [Fact]
    public void RejectsNegativeSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateEntry(sizeInBytes: -1));
    }

    [Fact]
    public void RetainsARegularFilePhysicalIdentity()
    {
        PhysicalEntryIdentity identity = TestFiles.FileIdentity("entry-1");

        FileEntry entry = new(
            TestFiles.CanonicalPathFor("report.pdf"),
            1,
            null,
            SampleInstant,
            identity);

        Assert.Same(identity, entry.PhysicalIdentity);
    }

    [Fact]
    public void RejectsADirectoryPhysicalIdentity()
    {
        PhysicalEntryIdentity identity = TestFiles.DirectoryIdentity("directory-1");

        Assert.Throws<ArgumentException>(
            () => new FileEntry(
                TestFiles.CanonicalPathFor("report.pdf"),
                1,
                null,
                SampleInstant,
                identity));
    }

    private static FileEntry CreateEntry(
        string name = "report.pdf",
        CanonicalPath? path = null,
        long sizeInBytes = 1024,
        DateTimeOffset? created = null,
        DateTimeOffset? modified = null)
    {
        DateTimeOffset createdAt = created ?? SampleInstant;

        return new FileEntry(
            path ?? TestFiles.CanonicalPathFor(name),
            sizeInBytes,
            createdAt,
            modified ?? createdAt);
    }
}
