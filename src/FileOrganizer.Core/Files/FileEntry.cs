using FileOrganizer.Core.Paths;

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
/// The path has already been canonicalized under explicit lexical semantics;
/// <see cref="Name"/> is derived from it rather than accepted as a second,
/// potentially inconsistent source of truth. The snapshot producer remains
/// responsible for confirming that the path denotes a real regular file and
/// that the metadata came from it. Core does not access the file system or
/// resolve links. Timestamp order is not constrained: copying, restoration and
/// clock skew can all produce surprising but real combinations.
/// </remarks>
public sealed class FileEntry
{
    /// <summary>
    /// Creates a snapshot of a file.
    /// </summary>
    /// <param name="path">The canonical, fully qualified path of the file.</param>
    /// <param name="sizeInBytes">Size of the file in bytes.</param>
    /// <param name="created">
    /// Creation timestamp reported by the file system, or <see langword="null"/>
    /// when the file system did not report a trustworthy one.
    /// </param>
    /// <param name="modified">Last write timestamp reported by the file system.</param>
    public FileEntry(
        CanonicalPath path,
        long sizeInBytes,
        DateTimeOffset? created,
        DateTimeOffset modified)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeInBytes);

        if (path.IsRoot)
        {
            throw new ArgumentException("A file path must include a file name.", nameof(path));
        }

        Path = path;
        Name = path.FileName!;
        SizeInBytes = sizeInBytes;

        // Normalized on the way in, so a rule comparing dates can never depend
        // on the time zone of the machine that took the snapshot.
        CreatedUtc = created?.ToUniversalTime();
        ModifiedUtc = modified.ToUniversalTime();

        int extensionStart = FindExtensionStart(Name);
        if (extensionStart < 0)
        {
            NameWithoutExtension = Name;
            Extension = string.Empty;
        }
        else
        {
            NameWithoutExtension = Name[..extensionStart];
            Extension = Name[extensionStart..];
        }
    }

    /// <summary>Gets the canonical, fully qualified path of the file.</summary>
    public CanonicalPath Path { get; }

    /// <summary>Gets the canonical path spelling for display and audit output.</summary>
    public string FullPath => Path.Value;

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
