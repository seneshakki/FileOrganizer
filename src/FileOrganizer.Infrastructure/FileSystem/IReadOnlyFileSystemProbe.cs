namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>
/// The narrow set of physical file-system reads used by the source scanner.
/// Kept internal so planning code can never acquire mutation capabilities.
/// </summary>
internal interface IReadOnlyFileSystemProbe
{
    FileAttributes GetAttributes(string path);

    IEnumerable<string> EnumerateEntries(string directoryPath);

    ReadOnlyFileSnapshot ReadFileSnapshot(string path);
}

internal readonly record struct ReadOnlyFileMetadata(
    long Length,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset ModifiedUtc);

/// <summary>
/// A second attribute observation taken immediately beside metadata capture.
/// Metadata is absent when that observation shows an unsafe entry type, so the
/// adapter never proceeds to file metadata through a known link/device.
/// </summary>
internal readonly record struct ReadOnlyFileSnapshot(
    FileAttributes Attributes,
    ReadOnlyFileMetadata? Metadata);
