using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhotoOrganizer;

internal static class FileIdentity
{
    public static string DirectoryPath(string path)
    {
        using var handle = CreateFile(ExtendedPath(path), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw NativeError();
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0) throw NativeError();
        string full = buffer.ToString();
        return full.StartsWith(@"\\?\") ? full[4..] : full;
    }
    public static string Read(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(handle);
    }
    internal static string Read(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw NativeError();
        return $"{info.VolumeSerialNumber:X8}-{info.FileIndexHigh:X8}{info.FileIndexLow:X8}-{info.CreationTimeHigh:X8}{info.CreationTimeLow:X8}";
    }
    public static async Task MoveVerifiedAsync(string source, string destination, string identity, string hash,
        CancellationToken token, Action<int>? onRead = null)
    {
        PathSafety.CheckAncestors(source);
        PathSafety.CheckAncestors(Path.GetDirectoryName(destination)!);
        if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(hash)) throw new IOException("MissingIdentity");
        // Keep the verified NTFS file open with DELETE access while excluding other writers and renamers.
        using var handle = CreateFile(ExtendedPath(source), 0x80010000, 1, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) throw NativeError();
        using var stream = new FileStream(handle, FileAccess.Read);
        if (Read(handle) != identity) throw new IOException("Changed");
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[1024 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, token)) > 0)
        {
            hasher.AppendData(chunk, 0, count);
            onRead?.Invoke(count);
        }
        if (Convert.ToHexString(hasher.GetHashAndReset()) != hash) throw new IOException("Changed");
        token.ThrowIfCancellationRequested();
        var name = System.Text.Encoding.Unicode.GetBytes(ExtendedPath(destination));
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + 4;
        var buffer = new byte[nameOffset + name.Length];
        BitConverter.GetBytes(name.Length).CopyTo(buffer, lengthOffset);
        name.CopyTo(buffer, nameOffset);
        // FileRenameInfo with ReplaceIfExists=false provides a no-overwrite, handle-based rename.
        if (!SetFileInformationByHandle(handle, 3, buffer, (uint)buffer.Length)) throw NativeError();
    }
    private static IOException NativeError()
    {
        int error = Marshal.GetLastWin32Error();
        return new IOException(new Win32Exception(error).Message, unchecked((int)0x80070000) | error);
    }
    private static string ExtendedPath(string path)
    {
        string full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\") ? full : @"\\?\" + full;
    }
    public static bool DeleteOwnedTemporary(string path, string identity)
    {
        if (!File.Exists(path)) return true;
        if (identity.Length == 0) return false;
        PathSafety.CheckAncestors(path);
        using var handle = CreateFile(ExtendedPath(path), 0x00010080, 1, IntPtr.Zero, 3, 0x00200080, IntPtr.Zero);
        if (handle.IsInvalid) throw NativeError();
        if (Read(handle) != identity) return false;
        if (!SetFileInformationByHandle(handle, 4, [1], 1)) throw NativeError();
        return true;
    }
    public static async Task<string> HashAsync(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    public static async Task<bool> MatchesAsync(string path, string identity, string hash, CancellationToken token)
    {
        if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(hash) || !File.Exists(path)) return false;
        PathSafety.CheckAncestors(path);
        return Read(path) == identity && await HashAsync(path, token) == hash && Read(path) == identity;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Information
    {
        public uint Attributes, CreationTimeLow, CreationTimeHigh, AccessTimeLow, AccessTimeHigh,
            WriteTimeLow, WriteTimeHigh, VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte[] information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
