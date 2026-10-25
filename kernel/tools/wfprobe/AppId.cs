using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WinForward.Probe;

// 规则进程池里存的是 ALE_APP_ID 的小写 ASCII 形式。用户态用同一套 API 拿到同一串：
// FwpmGetAppIdFromFileName0 返回 UTF-16 的完整路径（\device\harddiskvolumeN\...），
// 驱动侧 collect_process_path 逐字符折成小写 ASCII，这里做同样的归一化。
internal static class AppId
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FwpByteBlob
    {
        public uint Size;
        public IntPtr Data;
    }

    [DllImport("fwpuclnt.dll", SetLastError = false)]
    private static extern uint FwpmGetAppIdFromFileName0([MarshalAs(UnmanagedType.LPWStr)] string fileName,
                                                         out IntPtr appId);

    [DllImport("fwpuclnt.dll")]
    private static extern void FwpmFreeMemory0(ref IntPtr memory);

    public static string ForCurrentProcess() =>
        ForPath(Environment.ProcessPath ?? throw new InvalidOperationException("拿不到当前进程路径"));

    public static string ForPath(string path)
    {
        IntPtr raw;
        uint result = FwpmGetAppIdFromFileName0(path, out raw);
        if (result != 0)
            throw new Win32Exception((int)result, $"FwpmGetAppIdFromFileName0 失败：{path}");

        try
        {
            FwpByteBlob blob = Marshal.PtrToStructure<FwpByteBlob>(raw);
            byte[] bytes = new byte[blob.Size];
            Marshal.Copy(blob.Data, bytes, 0, (int)blob.Size);

            // app id 是 UTF-16LE（末尾带 NUL）。这里只做 ASCII 大小写折叠，非 ASCII 原样保留；
            // BlobBuilder 负责按 UTF-8 写进进程池。中文用户名路径因此也能参与比较。
            string text = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            var builder = new StringBuilder(text.Length);
            foreach (char character in text)
                builder.Append(character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character);
            return builder.ToString();
        }
        finally
        {
            FwpmFreeMemory0(ref raw);
        }
    }
}
