using FileOrganizer.Core.Abstractions;
using FileOrganizer.Core.Files;

namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>
/// Reads the machine's real file system through <see cref="System.IO"/>.
/// </summary>
/// <remarks>
/// The reader is deliberately conservative about what it reports:
/// <list type="bullet">
///   <item>
///     Reparse points (symbolic links, junctions) are skipped. Following them
///     would let an enumeration loop forever, or wander outside the folder the
///     user actually asked to organize.
///   </item>
///   <item>
///     Hidden and system files are skipped. They are rarely what someone means
///     by "organize this folder", and touching them has the worst consequences
///     when a rule turns out to be wrong. Making this configurable is a
///     decision for the rule model, not a default of the reader.
///   </item>
///   <item>
///     Files that vanish or become unreadable mid-enumeration are dropped
///     rather than propagated. A folder being reorganized is a moving target,
///     and one unreadable file must not abort an entire run.
///   </item>
/// </list>
/// </remarks>
public sealed class PhysicalFileSystemReader : IFileSystemReader
{
    private const FileAttributes SkippedAttributes =
        FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;

    /// <inheritdoc />
    public bool DirectoryExists(string directoryPath) => Directory.Exists(directoryPath);

    /// <inheritdoc />
    /// <exception cref="ArgumentException">
    /// <paramref name="directoryPath"/> is empty or whitespace.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// <paramref name="directoryPath"/> does not exist.
    /// </exception>
    public IEnumerable<FileEntry> EnumerateFiles(string directoryPath, bool recursive)
    {
        // Validated here rather than in the iterator below, so a caller that
        // passes a bad path is told immediately instead of at the first
        // MoveNext, possibly far away from the mistake.
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Directory does not exist: {directoryPath}");
        }

        return EnumerateFilesIterator(directoryPath, recursive);
    }

    private static IEnumerable<FileEntry> EnumerateFilesIterator(
        string directoryPath,
        bool recursive)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = recursive,
            AttributesToSkip = SkippedAttributes,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };

        foreach (string path in Directory.EnumerateFiles(directoryPath, "*", options))
        {
            FileEntry? entry = TryReadSnapshot(path);
            if (entry is not null)
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Reads one file's metadata, or returns <see langword="null"/> if it is no
    /// longer readable.
    /// </summary>
    private static FileEntry? TryReadSnapshot(string path)
    {
        try
        {
            FileInfo info = new(path);

            // CreationTimeUtc is not meaningful on every platform: Linux file
            // systems commonly report the inode change time or a placeholder.
            // Rules built on creation dates need to account for that.
            return new FileEntry(
                info.FullName,
                info.Name,
                info.Length,
                new DateTimeOffset(info.CreationTimeUtc),
                new DateTimeOffset(info.LastWriteTimeUtc));
        }
        catch (IOException)
        {
            // Deleted or moved between enumeration and inspection.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
