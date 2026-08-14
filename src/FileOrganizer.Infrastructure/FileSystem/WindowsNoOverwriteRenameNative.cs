using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>The single production user-file mutation boundary for Windows moves.</summary>
internal static class WindowsNoOverwriteRenameNative
{
    internal const int ErrorFileExists = 80;
    internal const int ErrorAlreadyExists = 183;

    [DllImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInformationClass fileInformationClass,
        nint fileInformation,
        uint bufferSize);

    internal static WindowsRenameResult Rename(
        SafeFileHandle sourceHandle,
        WindowsRenameBuffer target)
    {
        bool succeeded = SetFileInformationByHandle(
            sourceHandle,
            FileInformationClass.FileRenameInfo,
            target.Pointer,
            target.Size);

        return succeeded
            ? WindowsRenameResult.Success
            : new WindowsRenameResult(false, Marshal.GetLastPInvokeError());
    }

    private enum FileInformationClass
    {
        FileRenameInfo = 3,
    }
}

internal readonly record struct WindowsRenameResult(bool Succeeded, int ErrorCode)
{
    internal static WindowsRenameResult Success { get; } = new(true, 0);
}

/// <summary>A preallocated x64/x86 FILE_RENAME_INFO buffer.</summary>
internal sealed class WindowsRenameBuffer : IDisposable
{
    private static readonly int RootDirectoryOffset = Align(sizeof(byte), IntPtr.Size);
    private static readonly int FileNameLengthOffset = RootDirectoryOffset + IntPtr.Size;
    private static readonly int FileNameOffset = FileNameLengthOffset + sizeof(uint);
    private static readonly int StructureSize = Align(
        FileNameOffset + sizeof(char),
        IntPtr.Size);

    private nint _pointer;

    internal WindowsRenameBuffer(string absoluteTargetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteTargetPath);

        ReadOnlySpan<byte> fileName = MemoryMarshal.AsBytes(absoluteTargetPath.AsSpan());
        // Match the SDK sizeof(FILE_RENAME_INFO) plus the complete variable
        // filename payload. The extra aligned bytes are intentionally zero.
        Size = checked((uint)(StructureSize + fileName.Length));
        _pointer = Marshal.AllocHGlobal(checked((int)Size));

        try
        {
            byte[] buffer = new byte[Size];
            buffer[0] = 0; // ReplaceIfExists = FALSE.
            BinaryPrimitives.WriteUInt32LittleEndian(
                buffer.AsSpan(FileNameLengthOffset, sizeof(uint)),
                checked((uint)fileName.Length));
            fileName.CopyTo(buffer.AsSpan(FileNameOffset));
            Marshal.Copy(buffer, 0, _pointer, buffer.Length);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal nint Pointer => _pointer != 0
        ? _pointer
        : throw new ObjectDisposedException(nameof(WindowsRenameBuffer));

    internal uint Size { get; }

    public void Dispose()
    {
        nint pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer != 0)
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static int Align(int value, int alignment) =>
        checked((value + alignment - 1) & ~(alignment - 1));
}
