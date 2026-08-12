using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Planning;

namespace FileOrganizer.Core.Tests.Planning;

public sealed class SourcePreconditionTests
{
    [Fact]
    public void RestoresNormalizedImmutableMetadataWithoutFileSystemAccess()
    {
        CanonicalPath path = TestFiles.Paths.CanonicalizeAbsolute("/inbox/file.txt").Path!;
        PhysicalEntryIdentity identity = new(
            "test-id",
            1,
            "volume",
            "entry",
            PhysicalEntryKind.RegularFile);
        DateTimeOffset created = new(2026, 8, 12, 12, 0, 0, TimeSpan.FromHours(3));
        DateTimeOffset modified = created.AddMinutes(1);

        SourcePrecondition restored = new(path, 42, created, modified, identity);

        Assert.Same(path, restored.CanonicalPath);
        Assert.Equal(42, restored.Length);
        Assert.Equal(created.ToUniversalTime(), restored.CreatedUtc);
        Assert.Equal(modified.ToUniversalTime(), restored.ModifiedUtc);
        Assert.Same(identity, restored.PhysicalIdentity);
    }

    [Fact]
    public void RejectsInvalidRawPreconditions()
    {
        CanonicalPath path = TestFiles.Paths.CanonicalizeAbsolute("/inbox/file.txt").Path!;
        CanonicalPath root = TestFiles.Paths.CanonicalizeAbsolute("/").Path!;
        PhysicalEntryIdentity directory = new(
            "test-id",
            1,
            "volume",
            "entry",
            PhysicalEntryKind.Directory);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SourcePrecondition(path, -1, null, DateTimeOffset.UtcNow, null));
        Assert.Throws<ArgumentException>(
            () => new SourcePrecondition(root, 0, null, DateTimeOffset.UtcNow, null));
        Assert.Throws<ArgumentException>(
            () => new SourcePrecondition(path, 0, null, DateTimeOffset.UtcNow, directory));
    }
}
