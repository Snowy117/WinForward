using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinForward.Probe;

internal static class WfpSockets
{
    // _WSAIOW(IOC_VENDOR, 220/221/222)：IOC_IN | IOC_VENDOR | 功能号。
    public const uint SioQueryWfpConnectionRedirectRecords = 0x980000DC;
    public const uint SioQueryWfpConnectionRedirectContext = 0x980000DD;
    public const uint SioSetWfpConnectionRedirectRecords = 0x980000DE;

    private const int InitialBufferSize = 16 * 1024;
    private const int MaximumBufferSize = 256 * 1024;
    private const int ErrorInsufficientBuffer = 122;
    private const int Wsaemsgsize = 10040;
    private const int Wsaeinval = 10022;

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSAIoctl(IntPtr socket,
                                       uint ioControlCode,
                                       IntPtr inBuffer,
                                       uint inBufferSize,
                                       IntPtr outBuffer,
                                       uint outBufferSize,
                                       out uint bytesReturned,
                                       IntPtr overlapped,
                                       IntPtr completionRoutine);

    [DllImport("ws2_32.dll")]
    private static extern int WSAGetLastError();

    public static byte[] QueryRedirectContext(Socket socket) => Query(socket, SioQueryWfpConnectionRedirectContext);

    public static byte[] QueryRedirectRecords(Socket socket) => Query(socket, SioQueryWfpConnectionRedirectRecords);

    public static void SetRedirectRecords(Socket socket, byte[] records)
    {
        IntPtr buffer = Marshal.AllocHGlobal(records.Length);
        try
        {
            Marshal.Copy(records, 0, buffer, records.Length);
            int result = WSAIoctl(socket.Handle, SioSetWfpConnectionRedirectRecords, buffer, (uint)records.Length,
                                  IntPtr.Zero, 0, out _, IntPtr.Zero, IntPtr.Zero);
            if (result != 0)
                throw new Win32Exception(WSAGetLastError(), "SIO_SET_WFP_CONNECTION_REDIRECT_RECORDS 失败");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static byte[] Query(Socket socket, uint ioControlCode)
    {
        int size = InitialBufferSize;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int result = WSAIoctl(socket.Handle, ioControlCode, IntPtr.Zero, 0, buffer, (uint)size,
                                      out uint returned, IntPtr.Zero, IntPtr.Zero);
                if (result == 0)
                {
                    byte[] data = new byte[returned];
                    if (returned > 0)
                        Marshal.Copy(buffer, data, 0, (int)returned);
                    return data;
                }

                int error = WSAGetLastError();
                bool bufferTooSmall =
                    error == ErrorInsufficientBuffer || error == Wsaemsgsize || error == Wsaeinval;
                if (!bufferTooSmall)
                    throw new Win32Exception(error, $"WSAIoctl 0x{ioControlCode:X8} 失败");

                int next = returned > (uint)size ? (int)returned : size * 2;
                if (next > MaximumBufferSize)
                    next = MaximumBufferSize;
                if (next <= size)
                    throw new Win32Exception(error, $"WSAIoctl 0x{ioControlCode:X8} 缓冲区达到上限仍不足");
                size = next;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException($"WSAIoctl 0x{ioControlCode:X8} 重试多次仍失败");
    }
}
