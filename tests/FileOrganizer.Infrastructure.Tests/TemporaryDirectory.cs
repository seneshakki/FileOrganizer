namespace FileOrganizer.Infrastructure.Tests;

/// <summary>
/// A throwaway directory under the system temp path, removed when the test
/// that created it finishes.
/// </summary>
/// <remarks>
/// This is a test helper in a project whose whole subject is moving and
/// deleting files, so it refuses to delete anything that is not underneath the
/// system temp path. A mistake in a test must not be able to remove a real
/// folder such as Downloads or Documents.
/// </remarks>
internal sealed class TemporaryDirectory : IDisposable
{
    private TemporaryDirectory(string fullPath) => FullPath = fullPath;

    /// <summary>Gets the absolute path of the directory.</summary>
    public string FullPath { get; }

    public static TemporaryDirectory Create()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "FileOrganizer.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(path);
        return new TemporaryDirectory(path);
    }

    /// <summary>
    /// Writes a file relative to this directory, creating parent directories
    /// as needed, and returns its absolute path.
    /// </summary>
    public string WriteFile(string relativePath, string content = "")
    {
        string fullPath = Path.Combine(FullPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public void Dispose()
    {
        string tempRoot =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
            + Path.DirectorySeparatorChar;

        string target = Path.GetFullPath(FullPath);

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!target.StartsWith(tempRoot, comparison))
        {
            // Throwing from Dispose is normally poor form. Here it is the
            // point: silently continuing would mean a test is pointed at a
            // directory nobody intended to delete.
            throw new InvalidOperationException(
                $"Refusing to delete a directory outside the system temp path: {target}");
        }

        try
        {
            Directory.Delete(target, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone; nothing to clean up.
        }
    }
}
