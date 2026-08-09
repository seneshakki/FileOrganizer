using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>Read-only Win32 calls used by <see cref="WindowsPhysicalIdentityAdapter"/>.</summary>
internal static class WindowsFileIdentityNative
{
    internal const uint FileReadAttributes = 0x00000080;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint FileShareDelete = 0x00000004;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;

    internal const uint FileTypeDisk = 0x00000001;

    internal const uint FileAttributeDirectory = 0x00000010;
    internal const uint FileAttributeDevice = 0x00000040;
    internal const uint FileAttributeReparsePoint = 0x00000400;

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileType",
        ExactSpelling = true,
        SetLastError = true)]
    internal static extern uint GetFileType(SafeFileHandle file);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileAttributeTagInformation(
        SafeFileHandle file,
        FileInformationClass fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileStandardInformation(
        SafeFileHandle file,
        FileInformationClass fileInformationClass,
        out FileStandardInformation fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileBasicInformation(
        SafeFileHandle file,
        FileInformationClass fileInformationClass,
        out FileBasicInformation fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileIdentityInformation(
        SafeFileHandle file,
        FileInformationClass fileInformationClass,
        out FileIdentityInformation fileInformation,
        uint bufferSize);

    internal enum FileInformationClass
    {
        FileBasicInfo = 0,
        FileStandardInfo = 1,
        FileAttributeTagInfo = 9,
        FileIdInfo = 18,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct FileAttributeTagInformation
    {
        internal readonly uint FileAttributes;
        internal readonly uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct FileStandardInformation
    {
        internal readonly long AllocationSize;
        internal readonly long EndOfFile;
        internal readonly uint NumberOfLinks;
        internal readonly byte DeletePending;
        internal readonly byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct FileBasicInformation
    {
        internal readonly long CreationTime;
        internal readonly long LastAccessTime;
        internal readonly long LastWriteTime;
        internal readonly long ChangeTime;
        internal readonly uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct FileIdentityInformation
    {
        internal readonly ulong VolumeSerialNumber;
        internal readonly FileIdentifier128 FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct FileIdentifier128
    {
        internal readonly ulong FirstBytes;
        internal readonly ulong LastBytes;
    }
}
