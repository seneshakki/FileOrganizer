namespace FileOrganizer.Infrastructure.Tests;

public sealed class TemporaryDirectoryTests
{
    [Fact]
    public void RejectsParentTraversalBeforeWritingOutsideTheTemporaryDirectory()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string outsideName = Path.GetFileName(temp.FullPath) + ".txt";
        string relativeEscape = Path.Combine("..", outsideName);
        string escapedPath = Path.GetFullPath(Path.Combine(temp.FullPath, relativeEscape));

        Assert.Throws<ArgumentException>(() => temp.WriteFile(relativeEscape, "must not escape"));
        Assert.False(File.Exists(escapedPath));
    }

    [Fact]
    public void RejectsARootedPathEvenWhenItPointsUnderTheTemporaryDirectory()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string rootedPath = Path.Combine(temp.FullPath, "rooted.txt");

        Assert.Throws<ArgumentException>(() => temp.WriteFile(rootedPath, "must stay relative"));
        Assert.False(File.Exists(rootedPath));
    }

    [Fact]
    public void RejectsARelativePathThatResolvesToTheTemporaryDirectoryItself()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();

        Assert.Throws<ArgumentException>(() => temp.WriteFile(".", "not a file"));
    }
}
