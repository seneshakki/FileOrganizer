namespace FileOrganizer.Core.Physical;

/// <summary>Immutable file metadata captured from the same handle as identity.</summary>
public sealed record PhysicalFileMetadata
{
    public PhysicalFileMetadata(
        long length,
        DateTimeOffset? createdUtc,
        DateTimeOffset modifiedUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        Length = length;
        CreatedUtc = createdUtc?.ToUniversalTime();
        ModifiedUtc = modifiedUtc.ToUniversalTime();
    }

    /// <summary>Gets the file length in bytes.</summary>
    public long Length { get; }

    /// <summary>Gets the creation time in UTC when the file system supplied one.</summary>
    public DateTimeOffset? CreatedUtc { get; }

    /// <summary>Gets the last-write time in UTC.</summary>
    public DateTimeOffset ModifiedUtc { get; }
}
