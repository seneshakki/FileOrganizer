namespace FileOrganizer.Core.Physical;

/// <summary>
/// An immutable, path-independent identity issued by a platform file system.
/// </summary>
/// <remarks>
/// Identity guarantees are scoped by <see cref="Scheme"/> and
/// <see cref="SchemeVersion"/>. Consumers must compare every property; an
/// entry identifier alone is not globally unique.
/// </remarks>
public sealed record PhysicalEntryIdentity
{
    /// <summary>The scheme emitted by the Windows FILE_ID_INFO adapter.</summary>
    public const string WindowsFileIdScheme = "windows-file-id-128";

    /// <summary>The current representation version for the Windows scheme.</summary>
    public const int WindowsFileIdSchemeVersion = 1;

    public PhysicalEntryIdentity(
        string scheme,
        int schemeVersion,
        string volumeIdentity,
        string entryIdentifier,
        PhysicalEntryKind entryKind)
    {
        ValidateToken(scheme, nameof(scheme));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemeVersion);
        ValidateToken(volumeIdentity, nameof(volumeIdentity));
        ValidateToken(entryIdentifier, nameof(entryIdentifier));

        if (entryKind is not (PhysicalEntryKind.RegularFile or PhysicalEntryKind.Directory))
        {
            throw new ArgumentException(
                "Only ordinary files and directories can receive executable physical identity.",
                nameof(entryKind));
        }

        Scheme = scheme;
        SchemeVersion = schemeVersion;
        VolumeIdentity = volumeIdentity;
        EntryIdentifier = entryIdentifier;
        EntryKind = entryKind;
    }

    /// <summary>Gets the platform identity scheme.</summary>
    public string Scheme { get; }

    /// <summary>Gets the representation version within the identity scheme.</summary>
    public int SchemeVersion { get; }

    /// <summary>Gets the scheme-specific identity of the containing volume.</summary>
    public string VolumeIdentity { get; }

    /// <summary>Gets the stable entry identifier within the containing volume.</summary>
    public string EntryIdentifier { get; }

    /// <summary>Gets the physical kind bound into this identity.</summary>
    public PhysicalEntryKind EntryKind { get; }

    private static void ValidateToken(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (!StringComparer.Ordinal.Equals(value, value.Trim()))
        {
            throw new ArgumentException(
                "Identity components cannot have leading or trailing whitespace.",
                parameterName);
        }
    }
}
