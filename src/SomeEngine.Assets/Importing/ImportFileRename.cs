using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SomeEngine.Assets.Importing;

/// <summary>Changes a file name without invalidating readers of the preceding file.</summary>
internal static partial class ImportFileRename
{
    public static unsafe void Replace(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(source, destination, overwrite: true);
            return;
        }

        // FILE_RENAME_INFO with FileRenameInfoEx and replace + POSIX semantics keeps
        // existing handles valid, while new opens see only the replacement file.
        // https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information
        using SafeFileHandle handle = CreateFileW(Path.GetFullPath(source), 0x00010000,
            FileShare.ReadWrite | FileShare.Delete, 0, 3, 0, 0);
        if (handle.IsInvalid) ThrowFileError(source);
        destination = Path.GetFullPath(destination);
        int size = checked(sizeof(RenameInfo) + destination.Length * sizeof(char));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            buffer.AsSpan(0, size).Clear();
            fixed (byte* address = buffer)
            {
                var information = (RenameInfo*)address;
                information->Flags = 0x00000001 | 0x00000002;
                information->NameLength = checked((uint)(destination.Length * sizeof(char)));
                destination.AsSpan().CopyTo(new Span<char>(&information->Name, destination.Length));
                if (SetFileInformationByHandle(handle, 22, address, checked((uint)size)) == 0)
                    ThrowFileError(destination);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void ThrowFileError(string path)
    {
        int code = Marshal.GetLastPInvokeError();
        throw new IOException($"Cannot replace '{path}': {new Win32Exception(code).Message}",
            unchecked((int)(0x80070000u | (uint)code)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInfo
    {
        public uint Flags;
        public nint Root;
        public uint NameLength;
        public char Name;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string path, uint access, FileShare share,
        nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int SetFileInformationByHandle(SafeFileHandle file, int kind,
        void* information, uint size);
}
