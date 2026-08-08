using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Tests;

/// <summary>
/// Builds file snapshots for tests. Nothing here touches a disk.
/// </summary>
internal static class TestFiles
{
    private static readonly string InboxRoot = Path.GetFullPath("test-inbox");

    /// <summary>A fixed instant used wherever the exact time does not matter.</summary>
    public static readonly DateTimeOffset DefaultModified =
        new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds a host-native fully qualified path without touching the disk.
    /// </summary>
    public static string FullPathFor(string relativePath) =>
        Path.Combine(InboxRoot, relativePath);

    /// <param name="created">
    /// Defaults to <see langword="null"/>, meaning the file system did not
    /// report a creation time. Tests that care about creation dates say so.
    /// </param>
    public static FileEntry Create(
        string name = "report.pdf",
        long sizeInBytes = 1024,
        DateTimeOffset? created = null,
        DateTimeOffset? modified = null,
        string? fullPath = null) =>
        new(
            fullPath ?? FullPathFor(name),
            name,
            sizeInBytes,
            created,
            modified ?? DefaultModified);
}
