namespace FileOrganizer.Core.Files;

/// <summary>
/// An immutable snapshot of a single file, taken at one point in time.
/// </summary>
/// <remarks>
/// Rule matching and operation planning work exclusively on snapshots. That
/// keeps them deterministic and free of file system access, but it also means
/// a snapshot can be stale by the time an operation actually runs. Whoever
/// executes an operation is responsible for re-verifying the file first.
///
/// Core verifies only lexical invariants: the path is fully qualified in the
/// host platform's format and its final component is exactly <see cref="Name"/>.
/// The snapshot producer is responsible for confirming that the path denotes a
/// real regular file and that the metadata came from it. Core does not access
/// the file system, resolve links, enforce filesystem-specific naming rules or
/// establish containment within a root. Timestamp order is not constrained:
/// copying, restoration and clock skew can all produce surprising but real
/// combinations.
/// </remarks>
public sealed class FileEntry
{
    /// <summary>
    /// Creates a snapshot of a file.
    /// </summary>
    /// <param name="fullPath">
    /// Absolute path of the file in the host platform's own format.
    /// </param>
    /// <param name="name">
    /// File name including its extension, with no directory part.
    /// </param>
    /// <param name="sizeInBytes">Size of the file in bytes.</param>
    /// <param name="created">
    /// Creation timestamp reported by the file system, or <see langword="null"/>
    /// when the file system did not report a trustworthy one.
    /// </param>
    /// <param name="modified">Last write timestamp reported by the file system.</param>
    public FileEntry(
        string fullPath,
        string name,
        long sizeInBytes,
        DateTimeOffset? created,
        DateTimeOffset modified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeInBytes);

        if (fullPath.Contains('\0'))
        {
            throw new ArgumentException("A file path cannot contain a null character.", nameof(fullPath));
        }

        if (name.Contains('\0'))
        {
            throw new ArgumentException("A file name cannot contain a null character.", nameof(name));
        }

        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new ArgumentException("A file path must be fully qualified.", nameof(fullPath));
        }

        if (name is "." or ".." || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A file name must be a single file name with no directory part.",
                nameof(name));
        }

        if (!string.Equals(Path.GetFileName(fullPath), name, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The file name must exactly match the final component of the full path.",
                nameof(name));
        }

        FullPath = fullPath;
        Name = name;
        SizeInBytes = sizeInBytes;

        // Normalized on the way in, so a rule comparing dates can never depend
        // on the time zone of the machine that took the snapshot.
        CreatedUtc = created?.ToUniversalTime();
        ModifiedUtc = modified.ToUniversalTime();

        int extensionStart = FindExtensionStart(name);
        if (extensionStart < 0)
        {
            NameWithoutExtension = name;
            Extension = string.Empty;
        }
        else
        {
            NameWithoutExtension = name[..extensionStart];
            Extension = name[extensionStart..];
        }
    }

    /// <summary>Gets the fully qualified path of the file.</summary>
    public string FullPath { get; }

    /// <summary>Gets the file name including its extension.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets the file name without its extension, or the whole name when the
    /// file has no extension.
    /// </summary>
    public string NameWithoutExtension { get; }

    /// <summary>
    /// Gets the extension including its leading dot (for example
    /// <c>.txt</c>), or an empty string when the file has no extension.
    /// </summary>
    public string Extension { get; }

    /// <summary>Gets the size of the file in bytes.</summary>
    public long SizeInBytes { get; }

    /// <summary>
    /// Gets the creation timestamp in UTC, or <see langword="null"/> when the
    /// file system did not report one that can be trusted.
    /// </summary>
    /// <remarks>
    /// Not every platform records when a file was created. A rule that asks
    /// about creation time therefore has to cope with not being answered, so
    /// the absence is part of the model rather than a sentinel value that
    /// silently compares as very old.
    /// </remarks>
    public DateTimeOffset? CreatedUtc { get; }

    /// <summary>Gets the last write timestamp in UTC.</summary>
    public DateTimeOffset ModifiedUtc { get; }

    /// <summary>
    /// Returns the index of the dot that starts the extension, or <c>-1</c>
    /// when the name has no extension.
    /// </summary>
    /// <remarks>
    /// Two deliberate deviations from <see cref="Path.GetExtension(string)"/>:
    /// a leading dot never starts an extension, so <c>.gitignore</c> is a
    /// dotfile rather than a "gitignore" file; and a trailing dot does not
    /// either, so <c>report.</c> has no extension. Both cases would otherwise
    /// route files into surprising destination folders.
    /// </remarks>
    private static int FindExtensionStart(string name)
    {
        int lastDot = name.LastIndexOf('.');
        bool isLeadingDot = lastDot <= 0;
        bool isTrailingDot = lastDot == name.Length - 1;

        return isLeadingDot || isTrailingDot ? -1 : lastDot;
    }
}
