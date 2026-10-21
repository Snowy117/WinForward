using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinForward.Probe;

internal static class UdpSockets
{
    public const int IpWfpRedirectContext = 70;
    public const int IpWfpRedirectRecords = 60;

    private const uint SioGetExtensionFunctionPointer = 0xC8000006;
    private const int SockaddrStorageSize = 128;
    private const int CmsgHeaderSize = 16;

    private static readonly Guid WsaIdWSARecvMsg =
        new(0xf689d7c8, 0x6f1f, 0x436b, 0x8a, 0x53, 0xe5, 0x4f, 0xe3, 0x51, 0xc3, 0x22);

    public static Socket CreateListener(IPEndPoint endpoint)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpWfpRedirectContext, 1);
        socket.Bind(endpoint);
        return socket;
    }

    public static ReceivedDatagram Receive(Socket socket, int maxPayload, int maxControl, int timeoutMs)
    {
        if (!socket.Poll(timeoutMs * 1000, SelectMode.SelectRead))
            throw new TimeoutException($"等待 UDP datagram 超时（{timeoutMs} ms）");

        IntPtr function = IntPtr.Zero;
        Guid wsaRecvMsg = WsaIdWSARecvMsg;
        int ioctlResult = WSAIoctl(socket.Handle, SioGetExtensionFunctionPointer, ref wsaRecvMsg, 16,
                                   out function, IntPtr.Size, out _, IntPtr.Zero, IntPtr.Zero);
        if (ioctlResult != 0 || function == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "获取 WSARecvMsg 函数指针失败");

        var receive = Marshal.GetDelegateForFunctionPointer<WSARecvMsgDelegate>(function);

        byte[] payload = new byte[maxPayload];
        byte[] control = new byte[maxControl];
        byte[] name = new byte[SockaddrStorageSize];
        unsafe
        {
            fixed (byte* payloadPointer = payload)
            fixed (byte* controlPointer = control)
            fixed (byte* namePointer = name)
            {
                WSABUF payloadBuffer = new() { len = (uint)maxPayload, buf = (IntPtr)payloadPointer };
                WSABUF controlBuffer = new() { len = (uint)maxControl, buf = (IntPtr)controlPointer };
                WSAMSG message = new()
                {
                    name = (IntPtr)namePointer,
                    namelen = name.Length,
                    lpBuffers = (IntPtr)(&payloadBuffer),
                    dwBufferCount = 1,
                    Control = controlBuffer,
                    dwFlags = 0,
                };

                int result = receive(socket.Handle, (IntPtr)(&message), out uint received, IntPtr.Zero, IntPtr.Zero);
                if (result != 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WSARecvMsg 失败");

                int controlLength = (int)message.Control.len;
                byte[]? context = FindControl((IntPtr)controlPointer, controlLength, IpWfpRedirectContext);
                byte[]? records = FindControl((IntPtr)controlPointer, controlLength, IpWfpRedirectRecords);
                byte[] data = new byte[received];
                Array.Copy(payload, data, received);
                return new ReceivedDatagram(data, ParseEndpoint((IntPtr)namePointer), context, records);
            }
        }
    }

    private static IPEndPoint ParseEndpoint(IntPtr name)
    {
        SockaddrIn address = Marshal.PtrToStructure<SockaddrIn>(name);
        if (address.Family != (ushort)AddressFamily.InterNetwork)
            throw new NotSupportedException($"只支持 IPv4 端点（family={address.Family}）");
        byte[] bytes = BitConverter.GetBytes(address.Address);
        return new IPEndPoint(new IPAddress(bytes), (ushort)IPAddress.NetworkToHostOrder((short)address.Port));
    }

    private static byte[]? FindControl(IntPtr control, int length, int type)
    {
        int offset = 0;
        int alignment = IntPtr.Size;
        while (offset + CmsgHeaderSize <= length)
        {
            WSACMSGHDR header = Marshal.PtrToStructure<WSACMSGHDR>(IntPtr.Add(control, offset));
            int cmsgLength = (int)header.cmsg_len.ToUInt64();
            if (cmsgLength < CmsgHeaderSize || offset + cmsgLength > length)
                break;

            if (header.cmsg_level == 0 && header.cmsg_type == type)
            {
                int dataLength = cmsgLength - CmsgHeaderSize;
                byte[] data = new byte[dataLength];
                Marshal.Copy(IntPtr.Add(control, offset + CmsgHeaderSize), data, 0, dataLength);
                return data;
            }

            offset += (cmsgLength + alignment - 1) & ~(alignment - 1);
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockaddrIn
    {
        public ushort Family;
        public ushort Port;
        public uint Address;
        public ulong Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WSABUF
    {
        public uint len;
        public IntPtr buf;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WSAMSG
    {
        public IntPtr name;
        public int namelen;
        public IntPtr lpBuffers;
        public uint dwBufferCount;
        public WSABUF Control;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WSACMSGHDR
    {
        public UIntPtr cmsg_len;
        public int cmsg_level;
        public int cmsg_type;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WSARecvMsgDelegate(IntPtr socket,
                                           IntPtr message,
                                           out uint bytesReceived,
                                           IntPtr overlapped,
                                           IntPtr completionRoutine);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSAIoctl(IntPtr socket,
                                       uint ioControlCode,
                                       ref Guid inBuffer,
                                       int inBufferSize,
                                       out IntPtr outBuffer,
                                       int outBufferSize,
                                       out int bytesReturned,
                                       IntPtr overlapped,
                                       IntPtr completionRoutine);
}

internal sealed record ReceivedDatagram(byte[] Payload, IPEndPoint Source, byte[]? RedirectContext, byte[]? RedirectRecords);
