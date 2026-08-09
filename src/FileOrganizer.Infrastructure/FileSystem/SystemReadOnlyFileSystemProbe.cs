namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>Implements scanner reads with <see cref="System.IO"/>.</summary>
internal sealed class SystemReadOnlyFileSystemProbe : IReadOnlyFileSystemProbe
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public IEnumerable<string> EnumerateEntries(string directoryPath) =>
        Directory.EnumerateFileSystemEntries(directoryPath, "*", EnumerationOptions);

    public ReadOnlyFileMetadata ReadFileMetadata(string path)
    {
        FileInfo info = new(path);

        // Length forces FileInfo to obtain metadata and, unlike Exists, throws
        // when the entry vanished or cannot be read. Subsequent properties use
        // that snapshot. Execution still has to revalidate it immediately
        // before acting; source metadata is not a durable file identity.
        long length = info.Length;
        DateTimeOffset? createdUtc = OperatingSystem.IsLinux()
            ? null
            : new DateTimeOffset(info.CreationTimeUtc);

        return new ReadOnlyFileMetadata(
            length,
            createdUtc,
            new DateTimeOffset(info.LastWriteTimeUtc));
    }
}
