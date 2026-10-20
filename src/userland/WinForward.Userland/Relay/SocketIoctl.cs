using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinForward.Userland.Relay;

/// <summary>
/// <c>WSAIoctl</c> 里重定向相关的三个命令。编号是 <c>_WSAIOW(IOC_VENDOR, 220/221/222)</c>：
/// 读回原始目的地、读回 redirect records、把 records 挂到出站 socket 上。
/// </summary>
public static class SocketIoctl
{
    public const uint QueryRedirectRecords = 0x980000DC;
    public const uint QueryRedirectContext = 0x980000DD;
    public const uint SetRedirectRecordsCode = 0x980000DE;

    private const int InitialBufferSize = 16 * 1024;
    private const int MaximumBufferSize = 256 * 1024;
    private const int ErrorInsufficientBuffer = 122;
    private const int Wsaemsgsize = 10040;

    /// <summary>读回驱动挂上的 <c>redirect_context</c>。没有 context（不是被重定向的连接）时返回 null。</summary>
    public static byte[]? TryQueryRedirectContext(Socket socket, out string error)
    {
        error = string.Empty;
        try
        {
            return Query(socket, QueryRedirectContext);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            error = exception.Message;
            return null;
        }
    }

    /// <summary>读回原始连接上的 redirect records，供出站 socket 关联。失败时返回空数组（self-PID 闸仍然生效）。</summary>
    public static byte[] TryQueryRedirectRecords(Socket socket)
    {
        try
        {
            return Query(socket, QueryRedirectRecords);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return Array.Empty<byte>();
        }
    }

    public static void SetRedirectRecords(Socket socket, ReadOnlySpan<byte> records)
    {
        if (records.IsEmpty)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal(records.Length);
        try
        {
            CopyToUnmanaged(records, buffer);
            int result = WSAIoctl(socket.Handle, SetRedirectRecordsCode, buffer, (uint)records.Length,
                                  IntPtr.Zero, 0, out _, IntPtr.Zero, IntPtr.Zero);
            if (result != 0)
            {
                throw new Win32Exception(WSAGetLastError(), "SIO_SET_WFP_CONNECTION_REDIRECT_RECORDS 失败");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[] Query(Socket socket, uint ioControlCode)
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
                    return returned == 0 ? Array.Empty<byte>() : CopyFromUnmanaged(buffer, (int)returned);
                }

                int error = WSAGetLastError();
                // WSAEINVAL(10022) 对这两个查询的含义是「这条连接没有对应的上下文/记录」，不是缓冲区不足：
                // 当成缓冲区不足会一路重试到 256 KiB 才报「上限仍不足」，既误导又白白分配内存。
                bool bufferTooSmall = error == ErrorInsufficientBuffer || error == Wsaemsgsize;

                if (!bufferTooSmall)
                {
                    throw new Win32Exception(error, $"WSAIoctl 0x{ioControlCode:X8} 失败");
                }

                int next = returned > (uint)size ? (int)returned : size * 2;
                if (next > MaximumBufferSize)
                {
                    next = MaximumBufferSize;
                }

                if (next <= size)
                {
                    throw new Win32Exception(error, $"WSAIoctl 0x{ioControlCode:X8} 缓冲区达到上限仍不足");
                }

                size = next;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException($"WSAIoctl 0x{ioControlCode:X8} 重试多次仍失败");
    }

    private static void CopyToUnmanaged(ReadOnlySpan<byte> source, IntPtr destination)
    {
        byte[] managed = source.ToArray();
        Marshal.Copy(managed, 0, destination, managed.Length);
    }

    private static byte[] CopyFromUnmanaged(IntPtr source, int length)
    {
        byte[] data = new byte[length];
        Marshal.Copy(source, data, 0, length);
        return data;
    }

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
}
