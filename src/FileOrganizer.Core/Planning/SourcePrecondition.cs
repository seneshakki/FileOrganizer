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
    internal SourcePrecondition(CanonicalPath canonicalPath, FileEntry source)
    {
        CanonicalPath = canonicalPath;
        Length = source.SizeInBytes;
        CreatedUtc = source.CreatedUtc;
        ModifiedUtc = source.ModifiedUtc;
        PhysicalIdentity = source.PhysicalIdentity;
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
