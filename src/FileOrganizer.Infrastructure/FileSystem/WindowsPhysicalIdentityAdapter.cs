using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using Microsoft.Win32.SafeHandles;
using static FileOrganizer.Infrastructure.FileSystem.WindowsFileIdentityNative;

namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>
/// Reads Windows physical identity and metadata without following a final
/// reparse point or requesting any mutation access.
/// </summary>
/// <remarks>
/// <c>FILE_FLAG_OPEN_REPARSE_POINT</c> applies to the final path component.
/// This adapter does not claim physical containment through ancestor path
/// components; that remains an execution-time responsibility.
/// </remarks>
public sealed class WindowsPhysicalIdentityAdapter
{
    private const int ErrorInvalidFunction = 1;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidDrive = 15;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorNotSupported = 50;
    private const int ErrorNetworkAccessDenied = 65;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorCallNotImplemented = 120;
    private const int ErrorPrivilegeNotHeld = 1314;

    private const uint ShareAll = FileShareRead | FileShareWrite | FileShareDelete;
    private const uint NoFollowFlags = FileFlagBackupSemantics | FileFlagOpenReparsePoint;

    private readonly bool _isWindows;

    /// <summary>Creates an adapter using the current host capability.</summary>
    public WindowsPhysicalIdentityAdapter()
        : this(OperatingSystem.IsWindows())
    {
    }

    internal WindowsPhysicalIdentityAdapter(bool isWindows) => _isWindows = isWindows;

    /// <summary>
    /// Observes an existing regular file or directory through one safely
    /// disposed handle. Cancellation is checked between synchronous native reads.
    /// </summary>
    public PhysicalEntryObservation Observe(
        CanonicalPath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_isWindows)
        {
            return PhysicalEntryObservation.Unsupported(
                PhysicalEntryObservationReason.UnsupportedCapability);
        }

        if (path.Semantics.Syntax != PathSyntax.Windows)
        {
            return PhysicalEntryObservation.Unsupported(
                PhysicalEntryObservationReason.InvalidPath);
        }

        using SafeFileHandle handle = CreateFile(
            path.Value,
            FileReadAttributes,
            ShareAll,
            0,
            OpenExisting,
            NoFollowFlags,
            0);

        if (handle.IsInvalid)
        {
            int errorCode = Marshal.GetLastPInvokeError();
            cancellationToken.ThrowIfCancellationRequested();
            return FromOpenFailure(errorCode);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ObserveHandle(handle, cancellationToken);
    }

    private static PhysicalEntryObservation ObserveHandle(
        SafeFileHandle handle,
        CancellationToken cancellationToken)
    {
        if (GetFileType(handle) != FileTypeDisk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return PhysicalEntryObservation.Unsupported(
                PhysicalEntryObservationReason.UnsupportedEntryKind,
                PhysicalEntryKind.Unsupported);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadAttributeTag(handle, out FileAttributeTagInformation firstAttributes))
        {
            return QueryFailureAfterCancellationCheck(
                Marshal.GetLastPInvokeError(),
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        PhysicalEntryObservation? unsupported = ClassifyUnsupportedAttributes(firstAttributes);
        if (unsupported is not null)
        {
            return unsupported;
        }

        if (!GetFileStandardInformation(
                handle,
                FileInformationClass.FileStandardInfo,
                out FileStandardInformation standard,
                checked((uint)Marshal.SizeOf<FileStandardInformation>())))
        {
            return QueryFailureAfterCancellationCheck(
                Marshal.GetLastPInvokeError(),
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        PhysicalEntryKind entryKind = ClassifyOrdinaryKind(firstAttributes, standard);
        if (entryKind == PhysicalEntryKind.Unsupported)
        {
            return PhysicalEntryObservation.Inaccessible(
                PhysicalEntryObservationReason.EntryChanged);
        }

        if (standard.DeletePending != 0)
        {
            return PhysicalEntryObservation.Unsupported(
                PhysicalEntryObservationReason.DeletePending,
                entryKind);
        }

        if (!GetFileBasicInformation(
                handle,
                FileInformationClass.FileBasicInfo,
                out FileBasicInformation basic,
                checked((uint)Marshal.SizeOf<FileBasicInformation>())))
        {
            return QueryFailureAfterCancellationCheck(
                Marshal.GetLastPInvokeError(),
                cancellationToken,
                entryKind);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!AttributesAgree(firstAttributes.FileAttributes, basic.FileAttributes, entryKind))
        {
            return PhysicalEntryObservation.Inaccessible(
                PhysicalEntryObservationReason.EntryChanged,
                entryKind: entryKind);
        }

        PhysicalFileMetadata? metadata = null;
        if (entryKind == PhysicalEntryKind.RegularFile)
        {
            if (!TryCreateMetadata(standard, basic, out metadata))
            {
                return PhysicalEntryObservation.Unsupported(
                    PhysicalEntryObservationReason.MetadataUnavailable,
                    entryKind);
            }
        }

        if (!GetFileIdentityInformation(
                handle,
                FileInformationClass.FileIdInfo,
                out FileIdentityInformation identity,
                checked((uint)Marshal.SizeOf<FileIdentityInformation>())))
        {
            return QueryFailureAfterCancellationCheck(
                Marshal.GetLastPInvokeError(),
                cancellationToken,
                entryKind,
                metadata);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadAttributeTag(handle, out FileAttributeTagInformation finalAttributes))
        {
            return QueryFailureAfterCancellationCheck(
                Marshal.GetLastPInvokeError(),
                cancellationToken,
                entryKind,
                metadata);
        }

        cancellationToken.ThrowIfCancellationRequested();
        unsupported = ClassifyUnsupportedAttributes(finalAttributes);
        if (unsupported is not null)
        {
            return unsupported;
        }

        if (firstAttributes.FileAttributes != finalAttributes.FileAttributes
            || firstAttributes.ReparseTag != finalAttributes.ReparseTag
            || !AttributesAgree(finalAttributes.FileAttributes, basic.FileAttributes, entryKind))
        {
            return PhysicalEntryObservation.Inaccessible(
                PhysicalEntryObservationReason.EntryChanged,
                entryKind: entryKind,
                fileMetadata: metadata);
        }

        PhysicalEntryIdentity physicalIdentity = new(
            PhysicalEntryIdentity.WindowsFileIdScheme,
            PhysicalEntryIdentity.WindowsFileIdSchemeVersion,
            identity.VolumeSerialNumber.ToString("X16", CultureInfo.InvariantCulture),
            FormatFileIdentifier(identity.FileId),
            entryKind);

        return PhysicalEntryObservation.Success(physicalIdentity, metadata);
    }

    private static bool TryReadAttributeTag(
        SafeFileHandle handle,
        out FileAttributeTagInformation attributes) =>
        GetFileAttributeTagInformation(
            handle,
            FileInformationClass.FileAttributeTagInfo,
            out attributes,
            checked((uint)Marshal.SizeOf<FileAttributeTagInformation>()));

    private static PhysicalEntryObservation? ClassifyUnsupportedAttributes(
        FileAttributeTagInformation attributes)
    {
        if ((attributes.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            return PhysicalEntryObservation.Unsupported(
                PhysicalEntryObservationReason.ReparsePoint,
                PhysicalEntryKind.ReparsePoint);
        }

        if ((attributes.FileAttributes & FileAttributeDevice) != 0)
        {
            return PhysicalEntryObservation.Unsupported(
                PhysicalEntryObservationReason.UnsupportedEntryKind,
                PhysicalEntryKind.Unsupported);
        }

        return null;
    }

    private static PhysicalEntryKind ClassifyOrdinaryKind(
        FileAttributeTagInformation attributes,
        FileStandardInformation standard)
    {
        bool attributeDirectory =
            (attributes.FileAttributes & FileAttributeDirectory) != 0;
        bool standardDirectory = standard.Directory != 0;

        if (attributeDirectory != standardDirectory)
        {
            return PhysicalEntryKind.Unsupported;
        }

        return standardDirectory
            ? PhysicalEntryKind.Directory
            : PhysicalEntryKind.RegularFile;
    }

    private static bool AttributesAgree(
        uint firstAttributes,
        uint basicAttributes,
        PhysicalEntryKind entryKind)
    {
        const uint relevantAttributes =
            FileAttributeDirectory | FileAttributeDevice | FileAttributeReparsePoint;

        bool expectedDirectory = entryKind == PhysicalEntryKind.Directory;
        bool basicDirectory = (basicAttributes & FileAttributeDirectory) != 0;

        return (firstAttributes & relevantAttributes) == (basicAttributes & relevantAttributes)
            && expectedDirectory == basicDirectory;
    }

    private static bool TryCreateMetadata(
        FileStandardInformation standard,
        FileBasicInformation basic,
        out PhysicalFileMetadata? metadata)
    {
        metadata = null;
        if (standard.EndOfFile < 0 || basic.LastWriteTime <= 0)
        {
            return false;
        }

        try
        {
            DateTimeOffset? createdUtc = basic.CreationTime > 0
                ? FromWindowsFileTime(basic.CreationTime)
                : null;

            metadata = new PhysicalFileMetadata(
                standard.EndOfFile,
                createdUtc,
                FromWindowsFileTime(basic.LastWriteTime));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static DateTimeOffset FromWindowsFileTime(long fileTime) =>
        new(DateTime.FromFileTimeUtc(fileTime));

    private static string FormatFileIdentifier(FileIdentifier128 identifier)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[..8], identifier.FirstBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], identifier.LastBytes);
        return Convert.ToHexString(bytes);
    }

    internal static PhysicalEntryObservation FromOpenFailure(int errorCode) => errorCode switch
    {
        ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidDrive =>
            PhysicalEntryObservation.Missing(errorCode),
        ErrorAccessDenied or ErrorNetworkAccessDenied or ErrorPrivilegeNotHeld =>
            PhysicalEntryObservation.Inaccessible(
                PhysicalEntryObservationReason.AccessDenied,
                errorCode),
        ErrorSharingViolation or ErrorLockViolation =>
            PhysicalEntryObservation.Inaccessible(
                PhysicalEntryObservationReason.SharingViolation,
                errorCode),
        ErrorInvalidFunction
            or ErrorNotSupported
            or ErrorInvalidParameter
            or ErrorCallNotImplemented =>
            PhysicalEntryObservation.Unsupported(
                PhysicalEntryObservationReason.UnsupportedCapability,
                nativeErrorCode: errorCode),
        _ => PhysicalEntryObservation.Inaccessible(
            PhysicalEntryObservationReason.InputOutputFailure,
            errorCode),
    };

    private static PhysicalEntryObservation FromHandleQueryFailure(
        int errorCode,
        PhysicalEntryKind? entryKind = null,
        PhysicalFileMetadata? metadata = null) => errorCode switch
        {
            ErrorAccessDenied or ErrorNetworkAccessDenied or ErrorPrivilegeNotHeld =>
                PhysicalEntryObservation.Inaccessible(
                    PhysicalEntryObservationReason.AccessDenied,
                    errorCode,
                    entryKind,
                    metadata),
            ErrorSharingViolation or ErrorLockViolation =>
                PhysicalEntryObservation.Inaccessible(
                    PhysicalEntryObservationReason.SharingViolation,
                    errorCode,
                    entryKind,
                    metadata),
            ErrorInvalidFunction
                or ErrorNotSupported
                or ErrorInvalidParameter
                or ErrorCallNotImplemented =>
                PhysicalEntryObservation.Unsupported(
                    PhysicalEntryObservationReason.UnsupportedCapability,
                    entryKind,
                    metadata,
                    errorCode),
            _ => PhysicalEntryObservation.Inaccessible(
                PhysicalEntryObservationReason.InputOutputFailure,
                errorCode,
                entryKind,
                metadata),
        };

    private static PhysicalEntryObservation QueryFailureAfterCancellationCheck(
        int errorCode,
        CancellationToken cancellationToken,
        PhysicalEntryKind? entryKind = null,
        PhysicalFileMetadata? metadata = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return FromHandleQueryFailure(errorCode, entryKind, metadata);
    }
}
