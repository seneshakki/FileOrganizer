using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Abstractions;

/// <summary>
/// Read-only access to the file system.
/// </summary>
/// <remarks>
/// Reading and writing are separate ports on purpose. Rule evaluation and
/// operation planning are only ever handed a reader, so a defect in the
/// planner cannot modify a single file: the ability to do so is not reachable
/// from there. Write operations arrive with the operation executor.
/// </remarks>
public interface IFileSystemReader
{
    /// <summary>
    /// Determines whether the given directory exists.
    /// </summary>
    /// <param name="directoryPath">Absolute path of the directory.</param>
    bool DirectoryExists(string directoryPath);

    /// <summary>
    /// Lazily enumerates the files in a directory.
    /// </summary>
    /// <param name="directoryPath">Absolute path of the directory to read.</param>
    /// <param name="recursive">
    /// When <see langword="true"/>, files in sub-directories are included.
    /// </param>
    /// <remarks>
    /// Enumeration is lazy so that a directory holding a very large number of
    /// files does not have to be materialized in memory. Implementations skip
    /// entries they are not allowed to read rather than aborting: one
    /// unreadable sub-directory must not stop an entire run.
    /// </remarks>
    IEnumerable<FileEntry> EnumerateFiles(string directoryPath, bool recursive);
}
