using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinForward.Probe;

// UDP listener / client 的公共套接字层，IPv4 与 IPv6 走同一套代码。
internal static class UdpSockets
{
    public const int IpWfpRedirectContext = 70;

    public const int IpProtoIp = 0;
    public const int IpProtoIpv6 = 41;

    private const uint SioGetExtensionFunctionPointer = 0xC8000006;
    private const int SockaddrStorageSize = 128;
    private const int CmsgHeaderSize = 16;

    private static readonly Guid WsaIdWSARecvMsg =
        new(0xf689d7c8, 0x6f1f, 0x436b, 0x8a, 0x53, 0xe5, 0x4f, 0xe3, 0x51, 0xc3, 0x22);

    public static Socket CreateListener(IPEndPoint endpoint, bool dualMode)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (dualMode || endpoint.Address.Equals(IPAddress.IPv6Any))
                {
                    try
                    {
                        socket.DualMode = true;
                    }
                    catch (SocketException)
                    {
                    }
                }

                TrySetRedirectContext(socket, SocketOptionLevel.IPv6);
                TrySetRedirectContext(socket, SocketOptionLevel.IP);
            }
            else
            {
                TrySetRedirectContext(socket, SocketOptionLevel.IP);
            }

            socket.Bind(endpoint);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void TrySetRedirectContext(Socket socket, SocketOptionLevel level)
    {
        try
        {
            socket.SetSocketOption(level, (SocketOptionName)IpWfpRedirectContext, 1);
        }
        catch (SocketException)
        {
        }
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
                (byte[]? context, int level) = FindControl((IntPtr)controlPointer, controlLength);
                byte[] data = new byte[received];
                Array.Copy(payload, data, received);
                return new ReceivedDatagram(data, ParseEndpoint((IntPtr)namePointer), context, level);
            }
        }
    }

    private static IPEndPoint ParseEndpoint(IntPtr name)
    {
        ushort family = (ushort)Marshal.ReadInt16(name);
        if (family == (ushort)AddressFamily.InterNetwork)
        {
            SockaddrIn address = Marshal.PtrToStructure<SockaddrIn>(name);
            byte[] bytes = BitConverter.GetBytes(address.Address);
            int port = (ushort)IPAddress.NetworkToHostOrder((short)address.Port);
            return new IPEndPoint(new IPAddress(bytes), port);
        }

        if (family == (ushort)AddressFamily.InterNetworkV6)
        {
            SockaddrIn6 address = Marshal.PtrToStructure<SockaddrIn6>(name);
            int port = (ushort)IPAddress.NetworkToHostOrder((short)address.Port);
            return new IPEndPoint(new IPAddress(address.Address, address.ScopeId), port);
        }

        throw new NotSupportedException($"只支持 IPv4/IPv6 端点（family={family}）");
    }

    private static (byte[]? Context, int Level) FindControl(IntPtr control, int length)
    {
        int offset = 0;
        int alignment = IntPtr.Size;
        while (offset + CmsgHeaderSize <= length)
        {
            WSACMSGHDR header = Marshal.PtrToStructure<WSACMSGHDR>(IntPtr.Add(control, offset));
            int cmsgLength = (int)header.cmsg_len.ToUInt64();
            if (cmsgLength < CmsgHeaderSize || offset + cmsgLength > length)
                break;

            if (header.cmsg_type == IpWfpRedirectContext &&
                (header.cmsg_level == IpProtoIp || header.cmsg_level == IpProtoIpv6))
            {
                int dataLength = cmsgLength - CmsgHeaderSize;
                byte[] data = new byte[dataLength];
                Marshal.Copy(IntPtr.Add(control, offset + CmsgHeaderSize), data, 0, dataLength);
                return (data, header.cmsg_level);
            }

            offset += (cmsgLength + alignment - 1) & ~(alignment - 1);
        }

        return (null, -1);
    }

    public static IPEndPoint ParseEndPoint(string text)
    {
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close <= 1 || close + 1 >= text.Length || text[close + 1] != ':')
                throw new ArgumentException($"IPv6 地址应写成 [addr]:port：{text}");
            IPAddress address = IPAddress.Parse(text[1..close]);
            int port = int.Parse(text[(close + 2)..], System.Globalization.CultureInfo.InvariantCulture);
            return new IPEndPoint(address, port);
        }

        int separator = text.LastIndexOf(':');
        if (separator <= 0)
            throw new ArgumentException($"地址应为 HOST:PORT（IPv6 用 [addr]:port）：{text}");
        if (text.IndexOf(':') != separator)
            throw new ArgumentException($"IPv6 地址需要方括号：[addr]:port：{text}");
        return new IPEndPoint(IPAddress.Parse(text[..separator]),
                              int.Parse(text[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture));
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
    private struct SockaddrIn6
    {
        public ushort Family;
        public ushort Port;
        public uint FlowInfo;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Address;

        public uint ScopeId;
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

internal sealed record ReceivedDatagram(byte[] Payload, IPEndPoint Source, byte[]? RedirectContext, int ContextLevel);
