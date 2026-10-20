using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinForward.Userland.Policy;

/// <summary>
/// <c>fwpuclnt!FwpmGetAppIdFromFileName0</c> 的窄封装：DOS 路径 → ALE_APP_ID 的 UTF-16 设备路径。
/// </summary>
/// <remarks>
/// 只能在 Windows 上调用。返回的 <c>FWP_BYTE_BLOB</c> 里是宽字符形式的设备路径，可能带结尾 NUL；
/// 内存由 <c>FwpmFreeMemory0</c> 释放。任何一步失败都返回 null，由上层决定怎么报。
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsAppId
{
    internal static string? Resolve(string dosPath)
    {
        IntPtr pointer = IntPtr.Zero;
        uint status = FwpmGetAppIdFromFileName0(dosPath, out pointer);
        // 文档约定失败时输出为 NULL，但代码不依赖这个前提：只要拿到了指针，无论 status
        // 成功与否都走 finally 释放，避免失败路径漏掉 FwpmFreeMemory0。
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (status != 0)
            {
                return null;
            }

            FwpByteBlob blob = Marshal.PtrToStructure<FwpByteBlob>(pointer);
            if (blob.Data == IntPtr.Zero || blob.Size < 2)
            {
                return null;
            }

            string? text = Marshal.PtrToStringUni(blob.Data, (int)(blob.Size / 2));
            return text?.TrimEnd('\0');
        }
        finally
        {
            FwpmFreeMemory0(ref pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpByteBlob
    {
        internal uint Size;
        internal IntPtr Data;
    }

    [DllImport("fwpuclnt.dll", EntryPoint = "FwpmGetAppIdFromFileName0", CharSet = CharSet.Unicode)]
    private static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [DllImport("fwpuclnt.dll", EntryPoint = "FwpmFreeMemory0")]
    private static extern void FwpmFreeMemory0(ref IntPtr memory);
}
