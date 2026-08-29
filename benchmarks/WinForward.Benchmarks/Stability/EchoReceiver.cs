using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace WinForward.Benchmarks.Stability;

/// <summary>
/// The per-datagram wire convention shared by the UDP stability scenarios: a big-endian
/// 8-byte per-flow sequence number followed by a 4-byte flow id, carried at the front of
/// every soak datagram. Also defines the steady-state window test used to exclude
/// warmup/teardown boundary traffic from every metric.
/// </summary>
internal static class DatagramHeader
{
    public const int Size = 12;

    public static void Write(byte[] payload, long sequence, int flow)
    {
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(0, 8), (ulong)sequence);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8, 4), flow);
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out long sequence, out int flowId)
    {
        if (payload.Length < Size)
        {
            sequence = 0;
            flowId = 0;
            return false;
        }

        sequence = BinaryPrimitives.ReadInt64BigEndian(payload[..8]);
        flowId = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(8, 4));
        return true;
    }

    /// <summary>
    /// A datagram is inside the steady-state window iff its sequence is strictly greater than
    /// its flow's warmup-end marker. A null marker array (warmup still running) excludes
    /// everything; the array is frozen by the caller before publication.
    /// </summary>
    public static bool IsInWindow(long sequence, int flowId, long[]? markers) =>
        markers is not null && (uint)flowId < (uint)markers.Length && sequence > markers[flowId];
}

/// <summary>
/// The single echo destination shared by the UDP stability scenarios: parses the per-datagram
/// header, tracks per-flow reordering and duplicates with a 64-entry sequence ring, and echoes
/// each datagram back to its source. Received/OutOfOrder/Duplicates count only datagrams inside
/// the steady-state window (sequence above the per-flow marker published via
    /// <see cref="BeginWindow"/>); warmup and teardown-tail datagrams still feed the sequence ring
    /// but never touch the metrics. Buffer sizing is 16 MiB.
/// </summary>
internal sealed class EchoReceiver : IAsyncDisposable
{
    private const int ReceiveLoopCount = 4;

    private readonly Socket _socket;
    private readonly Task[] _loops;
    private readonly Dictionary<int, FlowState> _flows = new();
    private readonly Lock _trackingGate = new();
    private readonly bool[] _observed;
    private volatile long[]? _windowMarkers;
    private int _observedCount;

    public EchoReceiver(int flowCount)
    {
        _observed = new bool[flowCount];
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.ReceiveBufferSize = 16 << 20;
        _socket.Blocking = false;
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Endpoint = (IPEndPoint)_socket.LocalEndPoint!;
        _loops = new Task[ReceiveLoopCount];
        for (var index = 0; index < _loops.Length; index++)
        {
            _loops[index] = Task.Run(ReceiveLoopAsync);
        }
    }

    public IPEndPoint Endpoint { get; }

    public long Received { get; private set; }

    public long OutOfOrder { get; private set; }

    public long Duplicates { get; private set; }

    /// <summary>How many of the flow ids 0..flowCount-1 have been observed; grows monotonically.</summary>
    public int ObservedFlowCount => Volatile.Read(ref _observedCount);

    /// <summary>
    /// Publishes the frozen warmup-end markers. The volatile write releases the array contents,
    /// so every receive-loop reader that observes the field sees the fully initialized markers.
    /// </summary>
    public void BeginWindow(long[] markers) => _windowMarkers = markers;

    public async ValueTask DisposeAsync()
    {
        _socket.Dispose();
        await Task.WhenAll(_loops).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65_536];
        EndPoint anySource = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anySource).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var payload = buffer.AsMemory(0, result.ReceivedBytes);
            if (DatagramHeader.TryRead(payload.Span, out var sequence, out var flowId))
            {
                lock (_trackingGate)
                {
                    TrackDatagram(sequence, flowId);
                }
            }

            // Non-blocking sync echo first: a loopback UDP send completes inline, so the
            // receive loop never serializes behind an awaited reply completion. The rare
            // WouldBlock (kernel send queue full) falls back to the overlapped send.
            try
            {
                _ = _socket.SendTo(payload.Span, SocketFlags.None, result.RemoteEndPoint!);
                continue;
            }
            catch (SocketException)
            {
                // WouldBlock: the kernel send queue is momentarily full; use the overlapped send below.
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            try
            {
                _ = await _socket.SendToAsync(payload, SocketFlags.None, result.RemoteEndPoint!).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // A peer socket that vanished mid-echo must not end the measurement loop.
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private void TrackDatagram(long sequence, int flowId)
    {
        if (!_flows.TryGetValue(flowId, out var state))
        {
            state = new FlowState();
            _flows.Add(flowId, state);
            if ((uint)flowId < (uint)_observed.Length && !_observed[flowId])
            {
                _observed[flowId] = true;
                Interlocked.Increment(ref _observedCount);
            }
        }

        var inWindow = DatagramHeader.IsInWindow(sequence, flowId, _windowMarkers);
        if (sequence > state.LastSeen)
        {
            state.LastSeen = sequence;
        }
        else if (state.RingContains(sequence))
        {
            if (inWindow) Duplicates++;
        }
        else
        {
            if (inWindow) OutOfOrder++;
        }

        state.Push(sequence);
        if (inWindow) Received++;
    }

    private sealed class FlowState
    {
        private readonly long[] _recent = new long[64];
        private int _count;
        private int _next;

        public long LastSeen { get; set; }

        public bool RingContains(long sequence)
        {
            for (var index = 0; index < _count; index++)
            {
                if (_recent[index] == sequence) return true;
            }

            return false;
        }

        public void Push(long sequence)
        {
            _recent[_next] = sequence;
            _next = (_next + 1) % _recent.Length;
            if (_count < _recent.Length) _count++;
        }
    }
}
