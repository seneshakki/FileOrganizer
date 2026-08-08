using FileOrganizer.Core.Files;
using FileOrganizer.Infrastructure.FileSystem;

namespace FileOrganizer.Infrastructure.Tests.FileSystem;

/// <summary>
/// These tests do touch the real file system, but only inside a directory they
/// created under the system temp path. See <see cref="TemporaryDirectory"/>.
/// </summary>
public sealed class PhysicalFileSystemReaderTests
{
    private readonly PhysicalFileSystemReader _reader = new();

    [Fact]
    public void ReportsAnExistingDirectory()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();

        Assert.True(_reader.DirectoryExists(temp.FullPath));
    }

    [Fact]
    public void ReportsAMissingDirectory()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();

        Assert.False(_reader.DirectoryExists(Path.Combine(temp.FullPath, "absent")));
    }

    [Fact]
    public void ReturnsTheFilesInTheDirectory()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        temp.WriteFile("a.txt");
        temp.WriteFile("b.pdf");

        string[] names = NamesIn(temp, recursive: false);

        Assert.Equal(["a.txt", "b.pdf"], names);
    }

    [Fact]
    public void IgnoresSubdirectoriesWhenNotRecursive()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        temp.WriteFile("top.txt");
        temp.WriteFile(Path.Combine("nested", "deep.txt"));

        string[] names = NamesIn(temp, recursive: false);

        Assert.Equal(["top.txt"], names);
    }

    [Fact]
    public void IncludesSubdirectoriesWhenRecursive()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        temp.WriteFile("top.txt");
        temp.WriteFile(Path.Combine("nested", "deeper", "deep.txt"));

        string[] names = NamesIn(temp, recursive: true);

        Assert.Equal(["deep.txt", "top.txt"], names);
    }

    [Fact]
    public void ReadsTheMetadataOfAFile()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string path = temp.WriteFile("report.pdf", "12345");

        FileEntry entry = Assert.Single(_reader.EnumerateFiles(temp.FullPath, recursive: false));

        Assert.Equal("report.pdf", entry.Name);
        Assert.Equal(path, entry.FullPath);
        Assert.Equal(5, entry.SizeInBytes);
        Assert.Equal(".pdf", entry.Extension);
        Assert.Equal(TimeSpan.Zero, entry.ModifiedUtc.Offset);
    }

    [Fact]
    public void ReportsACreationTimeWhereThePlatformRecordsOne()
    {
        Assert.SkipWhen(
            OperatingSystem.IsLinux(),
            "Linux cannot be trusted to report a creation time, so none is reported.");

        using TemporaryDirectory temp = TemporaryDirectory.Create();
        temp.WriteFile("report.pdf", "12345");

        FileEntry entry = Assert.Single(_reader.EnumerateFiles(temp.FullPath, recursive: false));

        Assert.NotNull(entry.CreatedUtc);
        Assert.Equal(TimeSpan.Zero, entry.CreatedUtc.Value.Offset);
    }

    [Fact]
    public void SkipsHiddenFiles()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "The hidden file attribute can only be set on Windows.");

        using TemporaryDirectory temp = TemporaryDirectory.Create();
        temp.WriteFile("visible.txt");
        string hidden = temp.WriteFile("hidden.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        string[] names = NamesIn(temp, recursive: false);

        Assert.Equal(["visible.txt"], names);
    }

    [Fact]
    public void ReportsAMissingDirectoryBeforeEnumerationStarts()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string missing = Path.Combine(temp.FullPath, "absent");

        // The result is deliberately not enumerated: a lazy iterator would
        // defer the failure to the first MoveNext, far from the mistake.
        Assert.Throws<DirectoryNotFoundException>(
            () => _reader.EnumerateFiles(missing, recursive: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsABlankDirectoryPath(string directoryPath)
    {
        Assert.Throws<ArgumentException>(
            () => _reader.EnumerateFiles(directoryPath, recursive: false));
    }

    private string[] NamesIn(TemporaryDirectory temp, bool recursive) =>
        _reader.EnumerateFiles(temp.FullPath, recursive)
            .Select(entry => entry.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
