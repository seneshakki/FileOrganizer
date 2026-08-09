namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>
/// The narrow set of physical file-system reads used by the source scanner.
/// Kept internal so planning code can never acquire mutation capabilities.
/// </summary>
internal interface IReadOnlyFileSystemProbe
{
    FileAttributes GetAttributes(string path);

    IEnumerable<string> EnumerateEntries(string directoryPath);

    ReadOnlyFileMetadata ReadFileMetadata(string path);
}

internal readonly record struct ReadOnlyFileMetadata(
    long Length,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset ModifiedUtc);
