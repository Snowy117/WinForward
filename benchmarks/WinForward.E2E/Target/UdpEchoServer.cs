using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Target;

internal sealed class UdpEchoServer : IAsyncDisposable
{
    private const int MaxDatagramSize = 65536;
    private static readonly TimeSpan s_summaryInterval = TimeSpan.FromSeconds(1);

    private readonly Socket _socket;
    private readonly JsonlSink _ledger;
    private readonly int _receiverCount;
    private readonly EndPoint _sourceTemplate;
    private readonly SourceCensus[] _censuses;
    private readonly int[] _receiverStarts;
    private long _received;
    private long _undecodable;
    private long _bytes;
    private long _sendErrors;

    internal UdpEchoServer(EndPoint endPoint, JsonlSink ledger, int receiverCount)
    {
        _ledger = ledger;
        _receiverCount = receiverCount;
        _censuses = new SourceCensus[receiverCount];
        _receiverStarts = new int[receiverCount];
        for (var index = 0; index < _censuses.Length; index++)
        {
            _censuses[index] = new SourceCensus();
        }

        _sourceTemplate = Sockets.SourceTemplate(endPoint);

        _socket = Sockets.BindUdp(endPoint);
    }

    /// <summary>
    /// The receive loops that reached their receive call, one count per loop. A loop that never started
    /// serves no datagram, so the sum of these is the listener's real concurrency rather than the
    /// number it was configured with -- which is what <c>udpReceivers</c> publishes.
    /// </summary>
    internal long StartedReceivers
    {
        get
        {
            var started = 0L;
            foreach (var count in _receiverStarts)
            {
                started += count;
            }

            return started;
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var receivers = new Task[_receiverCount];
        for (var index = 0; index < receivers.Length; index++)
        {
            receivers[index] = ReceiveLoopAsync(index, _censuses[index], cancellationToken);
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
        writer.WriteNumber(ArmKeys.Ledger.TargetSummary.UdpTotals.UdpReceivers, StartedReceivers);
        writer.WriteNumber(ArmKeys.Ledger.TargetSummary.UdpTotals.Received, _received);
        writer.WriteNumber(ArmKeys.Ledger.TargetSummary.UdpTotals.Undecodable, _undecodable);
        writer.WriteNumber(ArmKeys.Ledger.TargetSummary.UdpTotals.Bytes, _bytes);
        writer.WriteNumber(ArmKeys.Ledger.TargetSummary.UdpTotals.SendErrors, _sendErrors);
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
                writer.WriteString(ArmKeys.Common.Record.Type, "udpSummary");
                writer.WriteNumber(ArmKeys.Ledger.UdpSummary.Received, _received);
                writer.WriteNumber(ArmKeys.Ledger.UdpSummary.Undecodable, _undecodable);
                writer.WriteNumber(ArmKeys.Ledger.UdpSummary.Bytes, _bytes);
                writer.WriteNumber(ArmKeys.Ledger.UdpSummary.Ticks, Stopwatch.GetTimestamp());
                writer.WriteStartArray(ArmKeys.Ledger.UdpSummary.Sources);
                foreach (var pair in ordered)
                {
                    writer.WriteStartObject();
                    writer.WriteString(ArmKeys.Ledger.UdpSummary.SourceEntry.Address, SourceCensus.ToAddress(pair.Key).ToString());
                    writer.WriteNumber(ArmKeys.Ledger.UdpSummary.SourceEntry.Port, pair.Key.Port);
                    writer.WriteNumber(ArmKeys.Ledger.UdpSummary.SourceEntry.Datagrams, pair.Value);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteNumber(ArmKeys.Ledger.UdpSummary.SourceOverflow, overflow);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(int index, SourceCensus census, CancellationToken cancellationToken)
    {
        // The loop books its own start before it can block: this is the count the ledger publishes as
        // udpReceivers, and it is a fact about this loop rather than about the array it was started from.
        Interlocked.Increment(ref _receiverStarts[index]);

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
}
