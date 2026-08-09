using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.Core.Tests;

/// <summary>
/// Builds file snapshots for tests. Nothing here touches a disk.
/// </summary>
internal static class TestFiles
{
    public static readonly PathPolicy Paths = new(
        PathSyntax.Posix,
        PathCaseSensitivity.Sensitive,
        PathUnicodeNormalization.None);

    /// <summary>A fixed instant used wherever the exact time does not matter.</summary>
    public static readonly DateTimeOffset DefaultModified =
        new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds a fixed POSIX path without touching a disk or consulting the host OS.
    /// </summary>
    public static string FullPathFor(string relativePath) =>
        "/test-inbox/" + relativePath.Replace('\\', '/').TrimStart('/');

    /// <summary>Builds a canonical path under the fixed test policy.</summary>
    public static CanonicalPath CanonicalPathFor(string relativePath)
    {
        PathResolution resolution = Paths.CanonicalizeAbsolute(FullPathFor(relativePath));
        return resolution.Path
            ?? throw new InvalidOperationException(resolution.Problem?.Description);
    }

    /// <param name="created">
    /// Defaults to <see langword="null"/>, meaning the file system did not
    /// report a creation time. Tests that care about creation dates say so.
    /// </param>
    public static FileEntry Create(
        string name = "report.pdf",
        long sizeInBytes = 1024,
        DateTimeOffset? created = null,
        DateTimeOffset? modified = null,
        CanonicalPath? path = null) =>
        new(
            path ?? CanonicalPathFor(name),
            sizeInBytes,
            created,
            modified ?? DefaultModified);
}
