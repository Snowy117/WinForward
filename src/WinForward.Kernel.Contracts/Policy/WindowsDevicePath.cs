using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace WinForward.Userland.Policy;

/// <summary>
/// 用文件句柄把 DOS 路径解析成 <c>ALE_APP_ID</c> 同形的 NT 设备路径。
/// </summary>
/// <remarks>
/// 相比 <c>FwpmGetAppIdFromFileName0</c>，句柄法对目录与长路径都能用：
/// <c>CreateFileW(FILE_FLAG_BACKUP_SEMANTICS)</c> 打开目标，
/// <c>GetFinalPathNameByHandleW(FILE_NAME_NORMALIZED | VOLUME_NAME_NT)</c> 给出规范化设备路径，
/// <c>GetFileInformationByHandle</c> 给出目录标记。任何一步失败都返回 null。
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsDevicePath
{
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint FileNameNormalized = 0x00000000;
    private const uint VolumeNameNt = 0x00000002;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint MaxPathChars = 32768;

    internal static ProcessPathResolution? Resolve(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        using var handle = CreateFileW(
            ToExtendedPath(fullPath), 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var required = GetFinalPathNameByHandleQuery(handle, IntPtr.Zero, 0, FileNameNormalized | VolumeNameNt);
        if (required == 0 || required > MaxPathChars)
        {
            return null;
        }

        var buffer = new char[required + 1];
        var written = GetFinalPathNameByHandleW(
            handle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameNt);
        if (written == 0 || written >= buffer.Length)
        {
            return null;
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            return null;
        }

        var devicePath = new string(buffer, 0, (int)written).TrimEnd('\\');
        if (devicePath.Length == 0 || devicePath[0] != '\\')
        {
            return null;
        }

        return new ProcessPathResolution(
            devicePath, (information.FileAttributes & FileAttributeDirectory) != 0);
    }

    private static string ToExtendedPath(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                   ? @"\\?\UNC\" + fullPath[2..]
                   : @"\\?\" + fullPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleQuery(
        SafeFileHandle file,
        IntPtr buffer,
        uint bufferLength,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        [Out] char[] buffer,
        uint bufferLength,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);
}
