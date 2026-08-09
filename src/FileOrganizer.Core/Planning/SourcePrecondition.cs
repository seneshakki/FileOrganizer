using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.Core.Planning;

/// <summary>
/// The immutable source metadata on which one plan item was based.
/// </summary>
/// <remarks>
/// Length and timestamps are preconditions, not a strong file identity. A
/// future executor must revalidate them immediately before acting and may add
/// a platform file identifier or inode to close more TOCTOU gaps.
/// </remarks>
public sealed class SourcePrecondition
{
    internal SourcePrecondition(CanonicalPath canonicalPath, FileEntry source)
    {
        CanonicalPath = canonicalPath;
        Length = source.SizeInBytes;
        CreatedUtc = source.CreatedUtc;
        ModifiedUtc = source.ModifiedUtc;
    }

    /// <summary>Gets the canonical source path used for planning.</summary>
    public CanonicalPath CanonicalPath { get; }

    /// <summary>Gets the source length used for planning.</summary>
    public long Length { get; }

    /// <summary>Gets the available creation timestamp used for planning.</summary>
    public DateTimeOffset? CreatedUtc { get; }

    /// <summary>Gets the modified timestamp used for planning.</summary>
    public DateTimeOffset ModifiedUtc { get; }
}
