using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Core.Planning;

/// <summary>
/// The immutable source metadata on which one plan item was based.
/// </summary>
/// <remarks>
/// Length and timestamps remain metadata preconditions rather than identity.
/// When <see cref="PhysicalIdentity"/> is available, an executor must revalidate
/// both it and the metadata immediately before acting. A snapshot alone does
/// not remove the TOCTOU window that remains until an adapter safely binds the
/// physical operation to its validated handle.
/// </remarks>
public sealed class SourcePrecondition
{
    /// <summary>
    /// Reconstructs a source precondition from a trusted, read-only metadata
    /// snapshot without consulting the file system.
    /// </summary>
    public SourcePrecondition(
        CanonicalPath canonicalPath,
        long length,
        DateTimeOffset? createdUtc,
        DateTimeOffset modifiedUtc,
        PhysicalEntryIdentity? physicalIdentity)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (canonicalPath.IsRoot)
        {
            throw new ArgumentException(
                "A source precondition path must include a file name.",
                nameof(canonicalPath));
        }

        if (physicalIdentity?.EntryKind is not (null or PhysicalEntryKind.RegularFile))
        {
            throw new ArgumentException(
                "A source precondition can retain only a regular-file physical identity.",
                nameof(physicalIdentity));
        }

        CanonicalPath = canonicalPath;
        Length = length;
        CreatedUtc = createdUtc?.ToUniversalTime();
        ModifiedUtc = modifiedUtc.ToUniversalTime();
        PhysicalIdentity = physicalIdentity;
    }

    internal SourcePrecondition(CanonicalPath canonicalPath, FileEntry source)
        : this(
            canonicalPath,
            source.SizeInBytes,
            source.CreatedUtc,
            source.ModifiedUtc,
            source.PhysicalIdentity)
    {
    }

    /// <summary>Gets the canonical source path used for planning.</summary>
    public CanonicalPath CanonicalPath { get; }

    /// <summary>Gets the source length used for planning.</summary>
    public long Length { get; }

    /// <summary>Gets the available creation timestamp used for planning.</summary>
    public DateTimeOffset? CreatedUtc { get; }

    /// <summary>Gets the modified timestamp used for planning.</summary>
    public DateTimeOffset ModifiedUtc { get; }

    /// <summary>
    /// Gets the strong physical identity captured with the source snapshot, or
    /// <see langword="null"/> for a preview-only source.
    /// </summary>
    public PhysicalEntryIdentity? PhysicalIdentity { get; }
}
