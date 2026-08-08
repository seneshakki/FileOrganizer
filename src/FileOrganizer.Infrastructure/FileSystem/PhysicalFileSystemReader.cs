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

            return new FileEntry(
                info.FullName,
                info.Name,
                info.Length,
                ReadCreationTime(info),
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

    /// <summary>
    /// Returns the creation timestamp, or <see langword="null"/> where the
    /// platform cannot be trusted to report one.
    /// </summary>
    /// <remarks>
    /// Windows records a creation time on NTFS, and macOS records st_birthtime,
    /// so both are reported as given. Linux only has a birth time on file
    /// systems and kernels that support statx; where it is missing, .NET
    /// substitutes a value derived from the change or write time. Managed code
    /// cannot tell the two apart, so rather than hand a rule a "creation date"
    /// that is quietly the modification date, Linux reports no creation time at
    /// all and conditions about it resolve as undetermined.
    /// </remarks>
    private static DateTimeOffset? ReadCreationTime(FileInfo info) =>
        OperatingSystem.IsLinux() ? null : new DateTimeOffset(info.CreationTimeUtc);
}
