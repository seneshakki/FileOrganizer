using FileOrganizer.Core.Files;

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
        // Path.GetExtension would report ".gitignore" as an extension, which
        // would file every dotfile under a folder named after itself.
        FileEntry entry = CreateEntry(name: name);

        Assert.Equal(string.Empty, entry.Extension);
        Assert.Equal(name, entry.NameWithoutExtension);
    }

    [Fact]
    public void TreatsTrailingDotAsHavingNoExtension()
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
        // Whether ".PDF" matches a ".pdf" rule is the matcher's decision.
        // Folding case here would be lossy, and a culture-aware fold would
        // additionally be wrong on a Turkish locale, where "I" lower-cases to
        // a dotless "i".
        FileEntry entry = CreateEntry(name: "INVOICE.PDF");

        Assert.Equal("INVOICE", entry.NameWithoutExtension);
        Assert.Equal(".PDF", entry.Extension);
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

        // Normalizing must not shift the instant, only its representation.
        Assert.Equal(createdInLocalOffset, createdUtc);
    }

    [Fact]
    public void KeepsAnUnreportedCreationTimeAsUnknown()
    {
        // Some platforms do not record when a file was created. The absence has
        // to survive into the model, because substituting any real timestamp
        // would let a date rule act on a number nobody measured.
        FileEntry entry = new("/inbox/report.pdf", "report.pdf", 1024, null, SampleInstant);

        Assert.Null(entry.CreatedUtc);
    }

    [Fact]
    public void KeepsTheValuesItWasGiven()
    {
        FileEntry entry = CreateEntry(
            fullPath: "/inbox/quarterly/report.pdf",
            name: "report.pdf",
            sizeInBytes: 4096);

        Assert.Equal("/inbox/quarterly/report.pdf", entry.FullPath);
        Assert.Equal("report.pdf", entry.Name);
        Assert.Equal(4096, entry.SizeInBytes);
    }

    [Fact]
    public void AcceptsAnEmptyFile()
    {
        FileEntry entry = CreateEntry(sizeInBytes: 0);

        Assert.Equal(0, entry.SizeInBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsBlankName(string name)
    {
        Assert.Throws<ArgumentException>(() => CreateEntry(name: name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsBlankFullPath(string fullPath)
    {
        Assert.Throws<ArgumentException>(() => CreateEntry(fullPath: fullPath));
    }

    [Fact]
    public void RejectsNullName()
    {
        Assert.Throws<ArgumentNullException>(() => CreateEntry(name: null!));
    }

    [Fact]
    public void RejectsNullFullPath()
    {
        // Constructed directly: the helper treats a null path as "use the
        // default", so it could not deliver a null to the constructor.
        Assert.Throws<ArgumentNullException>(
            () => new FileEntry(null!, "report.pdf", 1024, SampleInstant, SampleInstant));
    }

    [Fact]
    public void RejectsNegativeSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateEntry(sizeInBytes: -1));
    }

    private static FileEntry CreateEntry(
        string name = "report.pdf",
        string? fullPath = null,
        long sizeInBytes = 1024,
        DateTimeOffset? created = null,
        DateTimeOffset? modified = null)
    {
        DateTimeOffset createdAt = created ?? SampleInstant;

        return new FileEntry(
            fullPath ?? "/inbox/" + name,
            name,
            sizeInBytes,
            createdAt,
            modified ?? createdAt);
    }
}
