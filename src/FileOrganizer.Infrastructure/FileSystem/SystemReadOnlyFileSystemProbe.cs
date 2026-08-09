namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>Implements scanner and destination-inspection reads with <see cref="System.IO"/>.</summary>
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

    public ReadOnlyFileSnapshot ReadFileSnapshot(string path)
    {
        FileInfo info = new(path);

        // Re-observe the type immediately beside metadata capture. This closes
        // the broad race between directory enumeration and metadata inspection:
        // if the entry became a link, directory, or device, no further metadata
        // is requested through that known-unsafe entry. It is not a durable
        // identity check; M3B still has to open/revalidate safely before acting.
        FileAttributes attributes = info.Attributes;
        const FileAttributes unsafeAttributes =
            FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device;

        if ((attributes & unsafeAttributes) != 0)
        {
            return new ReadOnlyFileSnapshot(attributes, null);
        }

        // Unlike Exists, these FileInfo properties throw when the entry vanished
        // or cannot be read. They remain only a nearby metadata observation:
        // execution still has to revalidate immediately before acting because
        // this snapshot is not a durable file identity.
        long length = info.Length;
        DateTimeOffset? createdUtc = OperatingSystem.IsLinux()
            ? null
            : new DateTimeOffset(info.CreationTimeUtc);

        return new ReadOnlyFileSnapshot(
            attributes,
            new ReadOnlyFileMetadata(
                length,
                createdUtc,
                new DateTimeOffset(info.LastWriteTimeUtc)));
    }
}
