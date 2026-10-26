using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinForward.Userland.Relay;

/// <summary>
/// One datagram as the host listener receives it: the client source endpoint, the payload, and the
/// control buffer <c>WSARecvMsg</c> filled with the WFP redirect context.
/// </summary>
/// <remarks>
/// Both memories point into the receiver's reusable buffers and stay valid only until the handler's
/// <see cref="ValueTask"/> completes; a consumer that needs to keep either copies it.
/// </remarks>
public readonly record struct WfpUdpDatagram(IPEndPoint Source, ReadOnlyMemory<byte> Payload, ReadOnlyMemory<byte> Control);

/// <summary>
/// The blocking <c>WSARecvMsg</c> receive leg of the dual-mode host UDP socket.
/// </summary>
/// <remarks>
/// <para>
/// The WFP redirect context is a control message, and no managed receive API exposes it: only the
/// <c>WSARecvMsg</c> extension (resolved through <c>SIO_GET_EXTENSION_FUNCTION_POINTER</c>) returns the
/// payload, the source <c>sockaddr</c> and the cmsg chain in one call.
/// </para>
/// <para>
/// The call is synchronous, so it runs on one dedicated background thread rather than a pool thread
/// that would be parked for the process's lifetime. Cancellation is the owner's socket close: the
/// listener set owns the socket, and closing it makes the parked receive (or the preceding
/// <c>Socket.Poll</c>) return, which is why <see cref="Completion"/> is the join point the
/// owner awaits after it disposes the listener set. A short poll interval also lets the thread observe
/// cancellation on its own, so a start that failed before the sockets were published still exits.
/// </para>
/// <para>
/// An ICMP port-unreachable answering one of this socket's replies surfaces as
/// <c>WSAECONNRESET</c> on the next receive; that is a per-datagram skip, not a loop failure. Only the
/// socket-closed errors (which are the shutdown path) end the loop.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WfpUdpReceiver : IDisposable
{
    /// <summary>The largest UDP payload an IPv4 datagram can carry.</summary>
    public const int MaximumPayloadSize = 65_536;

    /// <summary>Control-buffer size the WFP suite uses for the same socket shape.</summary>
    public const int MaximumControlSize = 2_048;

    private const int SockaddrStorageSize = 128;
    private const int PollMicroseconds = 500_000;
    private const int WsaEintr = 10_004;
    private const int WsaEnotsock = 10_038;
    private static readonly TimeSpan s_joinTimeout = TimeSpan.FromSeconds(5);

    private const uint SioGetExtensionFunctionPointer = 0xC8000006;

    private static readonly Guid s_wsaIdWSARecvMsg =
        new(0xf689d7c8, 0x6f1f, 0x436b, 0x8a, 0x53, 0xe5, 0x4f, 0xe3, 0x51, 0xc3, 0x22);

    private readonly Socket _socket;
    private readonly WSARecvMsgDelegate _recvMsg;
    private readonly byte[] _payload = GC.AllocateUninitializedArray<byte>(MaximumPayloadSize);
    private readonly byte[] _control = new byte[MaximumControlSize];
    private readonly byte[] _name = new byte[SockaddrStorageSize];
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private bool _disposed;

    /// <summary>
    /// Resolves the <c>WSARecvMsg</c> function pointer once, so a platform without it fails at
    /// construction (where startup can report it) instead of on the first datagram.
    /// </summary>
    public WfpUdpReceiver(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
        _recvMsg = ResolveRecvMsg(socket);
    }

    /// <summary>Completes when the receive thread has left its loop; the owner's join point.</summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// Starts the receive thread. <paramref name="onDatagram"/> runs on that thread and must consume
    /// the datagram's memories before it returns; its <see cref="ValueTask"/> is awaited, so an
    /// asynchronous handler delays the next receive instead of racing the buffer's reuse.
    /// </summary>
    public void Start(Func<WfpUdpDatagram, ValueTask> onDatagram, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onDatagram);
        if (_thread is not null) throw new InvalidOperationException("The host UDP receiver is already started.");

        _thread = new Thread(() => Run(onDatagram, cancellationToken))
        {
            IsBackground = true,
            Name = "kernel.udp.receive",
        };
        _thread.Start();
    }

    private void Run(Func<WfpUdpDatagram, ValueTask> onDatagram, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && WaitReadable(cancellationToken))
            {
                if (!TryReceive(out var datagram, out var socketClosed))
                {
                    if (socketClosed) return;
                    continue;
                }

                ValueTask pending;
                try
                {
                    pending = onDatagram(datagram);
                }
                catch (Exception)
                {
                    // The handler owns its own containment; a fault that still escapes must not take
                    // the receive leg down for every other flow on this listener.
                    continue;
                }

                if (pending.IsCompletedSuccessfully)
                {
#pragma warning disable VSTHRD002 // Reading a completed ValueTask never waits; the branch condition above already proved completion.
                    pending.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                    continue;
                }

#pragma warning disable VSTHRD002 // The buffer is reused by the next receive, so this thread must not walk on before the handler released it; waiting here sits on the dedicated receive thread, whose continuations run on the pool, so it cannot deadlock.
                pending.AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            }
        }
        finally
        {
            _completion.TrySetResult();
        }
    }

    /// <summary>
    /// Parks until the socket is readable or the poll interval elapses, so cancellation is observed
    /// within one interval even before the owner closes the socket.
    /// </summary>
    private bool WaitReadable(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_socket.Poll(PollMicroseconds, SelectMode.SelectRead)) return true;
            }
            catch (SocketException)
            {
                // A transient poll failure is retried on the next interval.
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// One <c>WSARecvMsg</c>. Returns <see langword="false"/> when the call yielded nothing usable:
    /// <paramref name="socketClosed"/> distinguishes the shutdown path from a per-datagram skip.
    /// </summary>
    private unsafe bool TryReceive(out WfpUdpDatagram datagram, out bool socketClosed)
    {
        datagram = default;
        socketClosed = false;
        fixed (byte* payloadPointer = _payload)
        {
            fixed (byte* controlPointer = _control)
            {
                fixed (byte* namePointer = _name)
                {
                    WsaBuffer payloadBuffer = new() { Length = MaximumPayloadSize, Buffer = (IntPtr)payloadPointer };
                    WsaBuffer controlBuffer = new() { Length = MaximumControlSize, Buffer = (IntPtr)controlPointer };
                    WsaMessage message = new()
                    {
                        Name = (IntPtr)namePointer,
                        NameLength = _name.Length,
                        Buffers = (IntPtr)(&payloadBuffer),
                        BufferCount = 1,
                        Control = controlBuffer,
                        Flags = 0,
                    };

                    var result = _recvMsg(_socket.Handle, (IntPtr)(&message), out var received, IntPtr.Zero, IntPtr.Zero);
                    if (result != 0)
                    {
                        var error = Marshal.GetLastWin32Error();
                        // WSAENOTSOCK/WSAEINTR are the socket-closed errors, i.e. the owner's shutdown. Every
                        // other failure is a per-datagram skip: WSAECONNRESET (the peer's port is gone, from an
                        // ICMP port-unreachable answering one of our replies), WSAEWOULDBLOCK (the poll raced
                        // another reader) and WSAEMSGSIZE (a truncated datagram whose context cannot be trusted).
                        socketClosed = error is WsaEnotsock or WsaEintr;
                        return false;
                    }

                    if (received >= MaximumPayloadSize) return false;
                    if (!TryParseSource((IntPtr)namePointer, out var source)) return false;

                    datagram = new WfpUdpDatagram(
                        source,
                        new ReadOnlyMemory<byte>(_payload, 0, (int)received),
                        new ReadOnlyMemory<byte>(_control, 0, Math.Min((int)message.Control.Length, MaximumControlSize)));
                    return true;
                }
            }
        }
    }

    private static bool TryParseSource(IntPtr name, [NotNullWhen(true)] out IPEndPoint? source)
    {
        source = null;
        if (name == IntPtr.Zero) return false;

        var family = (ushort)Marshal.ReadInt16(name);
        switch (family)
        {
            case (ushort)AddressFamily.InterNetwork:
                {
                    var address = Marshal.PtrToStructure<SockaddrIn>(name);
                    var port = (ushort)IPAddress.NetworkToHostOrder((short)address.Port);
                    source = new IPEndPoint(new IPAddress(BitConverter.GetBytes(address.Address)), port);
                    return true;
                }

            case (ushort)AddressFamily.InterNetworkV6:
                {
                    var address = Marshal.PtrToStructure<SockaddrIn6>(name);
                    var port = (ushort)IPAddress.NetworkToHostOrder((short)address.Port);
                    source = new IPEndPoint(new IPAddress(address.Address, address.ScopeId), port);
                    return true;
                }

            default:
                return false;
        }
    }

    private static WSARecvMsgDelegate ResolveRecvMsg(Socket socket)
    {
        var wsaRecvMsg = s_wsaIdWSARecvMsg;
        var result = WSAIoctl(
            socket.Handle,
            SioGetExtensionFunctionPointer,
            ref wsaRecvMsg,
            16,
            out var function,
            IntPtr.Size,
            out _,
            IntPtr.Zero,
            IntPtr.Zero);
        if (result != 0 || function == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve the WSARecvMsg extension function pointer.");
        }

        return Marshal.GetDelegateForFunctionPointer<WSARecvMsgDelegate>(function);
    }

    /// <summary>
    /// Joins the receive thread. The socket is deliberately not closed here: the listener set owns it,
    /// and the owner closes it before this join so a parked receive cannot outlive the port.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _thread?.Join(s_joinTimeout);
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

    /// <summary>Win32 <c>WSABUF</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WsaBuffer
    {
        public uint Length;
        public IntPtr Buffer;
    }

    /// <summary>Win32 <c>WSAMSG</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WsaMessage
    {
        public IntPtr Name;
        public int NameLength;
        public IntPtr Buffers;
        public uint BufferCount;
        public WsaBuffer Control;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int WSARecvMsgDelegate(IntPtr socket, IntPtr message, out uint bytesReceived, IntPtr overlapped, IntPtr completionRoutine);

    [LibraryImport("ws2_32.dll", SetLastError = true)]
    private static partial int WSAIoctl(
        IntPtr socket,
        uint ioControlCode,
        ref Guid inBuffer,
        int inBufferSize,
        out IntPtr outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped,
        IntPtr completionRoutine);
}
