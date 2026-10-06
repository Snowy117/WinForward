using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Target;

internal sealed class UdpEchoServer : IAsyncDisposable
{
    private const int MaxDatagramSize = 65536;
    private const int SourceCapacity = 64;
    private static readonly TimeSpan s_summaryInterval = TimeSpan.FromSeconds(1);

    private readonly Socket _socket;
    private readonly JsonlSink _ledger;
    private readonly int _receiverCount;
    private readonly EndPoint _sourceTemplate;
    private readonly SourceCensus[] _censuses;
    private long _received;
    private long _undecodable;
    private long _bytes;
    private long _sendErrors;

    internal UdpEchoServer(EndPoint endPoint, JsonlSink ledger, int receiverCount)
    {
        _ledger = ledger;
        _receiverCount = receiverCount;
        _censuses = new SourceCensus[receiverCount];
        for (var index = 0; index < _censuses.Length; index++)
        {
            _censuses[index] = new SourceCensus();
        }

        _sourceTemplate = endPoint.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);

        _socket = new Socket(endPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, optionValue: true);
        _socket.Bind(endPoint);
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var receivers = new Task[_receiverCount];
        for (var index = 0; index < receivers.Length; index++)
        {
            receivers[index] = ReceiveLoopAsync(_censuses[index], cancellationToken);
        }

        var summary = SummarizeLoopAsync(cancellationToken);
        await Task.WhenAll(receivers).ConfigureAwait(false);
        await summary.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    internal void WriteTotals(Utf8JsonWriter writer)
    {
        writer.WriteNumber("received", _received);
        writer.WriteNumber("undecodable", _undecodable);
        writer.WriteNumber("bytes", _bytes);
        writer.WriteNumber("sendErrors", _sendErrors);
    }

    internal async ValueTask WriteSummaryAsync(CancellationToken cancellationToken)
    {
        var sources = new Dictionary<SourceKey, long>();
        var overflow = 0L;
        foreach (var census in _censuses)
        {
            census.Harvest(sources);
            overflow += census.HarvestUnplaced();
        }

        var ordered = new List<KeyValuePair<SourceKey, long>>(sources);
        ordered.Sort(static (left, right) =>
        {
            var byHigh = left.Key.High.CompareTo(right.Key.High);
            if (byHigh != 0)
            {
                return byHigh;
            }

            var byLow = left.Key.Low.CompareTo(right.Key.Low);
            return byLow != 0 ? byLow : left.Key.Port.CompareTo(right.Key.Port);
        });

        await _ledger.WriteAsync(
            writer =>
            {
                writer.WriteString("type", "udpSummary");
                writer.WriteNumber("received", _received);
                writer.WriteNumber("undecodable", _undecodable);
                writer.WriteNumber("bytes", _bytes);
                writer.WriteNumber("ticks", Stopwatch.GetTimestamp());
                writer.WriteStartArray("sources");
                foreach (var pair in ordered)
                {
                    writer.WriteStartObject();
                    writer.WriteString("address", ToAddress(pair.Key).ToString());
                    writer.WriteNumber("port", pair.Key.Port);
                    writer.WriteNumber("datagrams", pair.Value);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteNumber("sourceOverflow", overflow);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(SourceCensus census, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxDatagramSize];
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, _sourceTemplate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (SocketException)
            {
                return;
            }

            Interlocked.Increment(ref _received);
            Interlocked.Add(ref _bytes, result.ReceivedBytes);
            census.Record(result.RemoteEndPoint);

            if (!FrameCodec.TryDecode(buffer.AsSpan(0, result.ReceivedBytes), out _, out _, out _))
            {
                Interlocked.Increment(ref _undecodable);
                continue;
            }

            try
            {
                await _socket.SendToAsync(buffer.AsMemory(0, result.ReceivedBytes), SocketFlags.None, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                Interlocked.Increment(ref _sendErrors);
            }
        }
    }

    private async Task SummarizeLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(s_summaryInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await WriteSummaryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* the shutdown signal ended the summary loop; the totals are written by the runner */
        }
        catch (ObjectDisposedException)
        {
            /* the shutdown signal ended the summary loop; the totals are written by the runner */
        }
    }

    private static IPAddress ToAddress(SourceKey key)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, key.High);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], key.Low);
        return key.High == 0 && (key.Low >> 32) == 0xFFFF
            ? new IPAddress(bytes[12..])
            : new IPAddress(bytes);
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct SourceKey(ulong High, ulong Low, int Port);

    /// <summary>
    /// The source endpoints datagrams arrived from, one fixed table per receiver so a datagram costs neither
    /// an allocation nor a lock: only the owning receiver writes a table, and the summariser is the only
    /// reader. An endpoint keeps its slot for the life of the server, so a slot's identity is written once
    /// and its count only grows; <see cref="Harvest"/> publishes the delta since the previous interval and
    /// therefore reports the endpoints seen in that interval. A table that is full puts nothing on the wire:
    /// the datagram is counted as overflow instead of being silently dropped from the census.
    /// </summary>
    private sealed class SourceCensus
    {
        private readonly Slot[] _slots = new Slot[SourceCapacity];
        private readonly long[] _published = new long[SourceCapacity];
        private long _unplaced;
        private long _publishedUnplaced;

        internal void Record(EndPoint remote)
        {
            if (remote is not IPEndPoint endPoint || !TryGetIdentity(endPoint.Address, out var high, out var low))
            {
                Interlocked.Increment(ref _unplaced);
                return;
            }

            var port = endPoint.Port;
            var free = -1;
            for (var index = 0; index < _slots.Length; index++)
            {
                ref var slot = ref _slots[index];
                if (slot._port == port && slot._high == high && slot._low == low)
                {
                    slot._datagrams++;
                    return;
                }

                if (slot._port == 0 && free < 0)
                {
                    free = index;
                }
            }

            if (free < 0)
            {
                Interlocked.Increment(ref _unplaced);
                return;
            }

            ref var claimed = ref _slots[free];
            claimed._high = high;
            claimed._low = low;
            claimed._datagrams = 1;

            // A source port is never zero, so publishing the port last both marks the slot as claimed and
            // releases the identity and the count to the summariser without an atomic block.
            Volatile.Write(ref claimed._port, port);
        }

        internal void Harvest(Dictionary<SourceKey, long> totals)
        {
            for (var index = 0; index < _slots.Length; index++)
            {
                ref var slot = ref _slots[index];
                var port = Volatile.Read(ref slot._port);
                if (port == 0)
                {
                    continue;
                }

                var datagrams = Interlocked.Read(ref slot._datagrams);
                var delta = datagrams - _published[index];
                if (delta <= 0)
                {
                    continue;
                }

                _published[index] = datagrams;
                var key = new SourceKey(Volatile.Read(ref slot._high), Volatile.Read(ref slot._low), port);
                totals[key] = totals.TryGetValue(key, out var existing) ? existing + delta : delta;
            }
        }

        internal long HarvestUnplaced()
        {
            var unplaced = Interlocked.Read(ref _unplaced);
            var delta = unplaced - _publishedUnplaced;
            if (delta <= 0)
            {
                return 0;
            }

            _publishedUnplaced = unplaced;
            return delta;
        }

        private static bool TryGetIdentity(IPAddress address, out ulong high, out ulong low)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (!address.TryWriteBytes(bytes, out var written))
            {
                high = 0;
                low = 0;
                return false;
            }

            if (written == 4)
            {
                // Normalise IPv4 into its IPv4-mapped IPv6 form: one comparison then covers a v4 client and
                // the same client seen through a listener bound to IPv6Any.
                bytes[12] = bytes[0];
                bytes[13] = bytes[1];
                bytes[14] = bytes[2];
                bytes[15] = bytes[3];
                bytes[..12].Clear();
                bytes[10] = 0xFF;
                bytes[11] = 0xFF;
            }

            high = BinaryPrimitives.ReadUInt64BigEndian(bytes);
            low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
            return true;
        }

        [StructLayout(LayoutKind.Auto)]
        private struct Slot
        {
            internal ulong _high;
            internal ulong _low;
            internal int _port;
            internal long _datagrams;
        }
    }
}
