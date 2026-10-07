using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Target;

/// <summary>
/// The key names one dns totals block is written with, in write order. The same counters reach the
/// ledger at two levels -- a <c>dnsSummary</c> record's own root and the <c>targetSummary</c> containers
/// <c>dns</c> and <c>dnsAlt</c> -- and a leaf written at another level is another constant (D14.17), so
/// the writer takes the level's set instead of spelling a name at the call site. Both containers of the
/// target level are the same depth and the same writer, so they share <see cref="Target"/>.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct DnsTotalsKeys
{
    private DnsTotalsKeys(
        string port,
        string udpQueries,
        string udpAnswers,
        string udpEmptyAnswers,
        string udpMalformed,
        string udpSendErrors,
        string tcpQueries,
        string tcpAnswers,
        string tcpEmptyAnswers,
        string tcpMalformed,
        string tcpTruncatedFrames,
        string tcpConnections,
        string tcpAcceptErrors,
        string tcpAborted)
    {
        Port = port;
        UdpQueries = udpQueries;
        UdpAnswers = udpAnswers;
        UdpEmptyAnswers = udpEmptyAnswers;
        UdpMalformed = udpMalformed;
        UdpSendErrors = udpSendErrors;
        TcpQueries = tcpQueries;
        TcpAnswers = tcpAnswers;
        TcpEmptyAnswers = tcpEmptyAnswers;
        TcpMalformed = tcpMalformed;
        TcpTruncatedFrames = tcpTruncatedFrames;
        TcpConnections = tcpConnections;
        TcpAcceptErrors = tcpAcceptErrors;
        TcpAborted = tcpAborted;
    }

    /// <summary>The key set of a <c>dnsSummary</c> record's own totals.</summary>
    internal static DnsTotalsKeys Summary { get; } = new(
        ArmKeys.Ledger.DnsSummary.Port,
        ArmKeys.Ledger.DnsSummary.UdpQueries,
        ArmKeys.Ledger.DnsSummary.UdpAnswers,
        ArmKeys.Ledger.DnsSummary.UdpEmptyAnswers,
        ArmKeys.Ledger.DnsSummary.UdpMalformed,
        ArmKeys.Ledger.DnsSummary.UdpSendErrors,
        ArmKeys.Ledger.DnsSummary.TcpQueries,
        ArmKeys.Ledger.DnsSummary.TcpAnswers,
        ArmKeys.Ledger.DnsSummary.TcpEmptyAnswers,
        ArmKeys.Ledger.DnsSummary.TcpMalformed,
        ArmKeys.Ledger.DnsSummary.TruncatedFrames,
        ArmKeys.Ledger.DnsSummary.TcpConnections,
        ArmKeys.Ledger.DnsSummary.AcceptErrors,
        ArmKeys.Ledger.DnsSummary.TcpAborted);

    /// <summary>The key set of the same block one level down, under <c>targetSummary/dns</c>.</summary>
    internal static DnsTotalsKeys Target { get; } = new(
        ArmKeys.Ledger.TargetSummary.DnsTotals.Port,
        ArmKeys.Ledger.TargetSummary.DnsTotals.UdpQueries,
        ArmKeys.Ledger.TargetSummary.DnsTotals.UdpAnswers,
        ArmKeys.Ledger.TargetSummary.DnsTotals.UdpEmptyAnswers,
        ArmKeys.Ledger.TargetSummary.DnsTotals.UdpMalformed,
        ArmKeys.Ledger.TargetSummary.DnsTotals.UdpSendErrors,
        ArmKeys.Ledger.TargetSummary.DnsTotals.TcpQueries,
        ArmKeys.Ledger.TargetSummary.DnsTotals.TcpAnswers,
        ArmKeys.Ledger.TargetSummary.DnsTotals.TcpEmptyAnswers,
        ArmKeys.Ledger.TargetSummary.DnsTotals.TcpMalformed,
        ArmKeys.Ledger.TargetSummary.DnsTotals.TruncatedFrames,
        ArmKeys.Ledger.TargetSummary.DnsTotals.TcpConnections,
        ArmKeys.Ledger.TargetSummary.DnsTotals.AcceptErrors,
        ArmKeys.Ledger.TargetSummary.DnsTotals.TcpAborted);

    internal string Port { get; }

    internal string UdpQueries { get; }

    internal string UdpAnswers { get; }

    internal string UdpEmptyAnswers { get; }

    internal string UdpMalformed { get; }

    internal string UdpSendErrors { get; }

    internal string TcpQueries { get; }

    internal string TcpAnswers { get; }

    internal string TcpEmptyAnswers { get; }

    internal string TcpMalformed { get; }

    internal string TcpTruncatedFrames { get; }

    internal string TcpConnections { get; }

    internal string TcpAcceptErrors { get; }

    internal string TcpAborted { get; }
}

internal sealed class DnsServer : IAsyncDisposable
{
    private const int MaxMessageLength = DnsWire.MaxMessageLength;

    private readonly Socket _udp;
    private readonly JsonlSink _ledger;
    private readonly int _workerCount;
    private readonly int _port;
    private readonly EndPoint _sourceTemplate;
    private readonly TcpAcceptLoop _acceptLoop;
    private long _udpQueries;
    private long _udpAnswers;
    private long _udpEmpty;
    private long _udpMalformed;
    private long _udpSendErrors;
    private long _tcpQueries;
    private long _tcpAnswers;
    private long _tcpEmpty;
    private long _tcpMalformed;
    private long _tcpTruncated;
    private long _tcpConnectionsAccepted;
    private long _tcpAborted;

    internal DnsServer(IPEndPoint endPoint, JsonlSink ledger, int workerCount)
        : this(Sockets.BindUdp(endPoint), new TcpAcceptLoop(Sockets.BindTcpListener(endPoint)), ledger, workerCount, endPoint.Port)
    {
    }

    /// <summary>
    /// The responder over sockets the caller already built. A refused accept cannot be produced from an
    /// endpoint -- the kernel decides when one happens -- so the one socket whose accept always fails
    /// has to arrive from outside, which is how the ledger's own evidence for
    /// <see cref="DnsTotalsKeys.TcpAcceptErrors"/> is driven.
    /// </summary>
    internal DnsServer(Socket udp, TcpAcceptLoop acceptLoop, JsonlSink ledger, int workerCount, int port)
    {
        _ledger = ledger;
        _workerCount = workerCount;
        _port = port;

        // The receive template only has to be the socket's own address family, so it is taken from the
        // socket rather than from an endpoint the caller would have to hand in twice.
        _sourceTemplate = Sockets.SourceTemplate(udp.LocalEndPoint ?? new IPEndPoint(IPAddress.Any, port));

        _udp = udp;
        _acceptLoop = acceptLoop;
    }

    public ValueTask DisposeAsync()
    {
        _udp.Dispose();
        _acceptLoop.Dispose();
        return ValueTask.CompletedTask;
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var workers = new Task[_workerCount + 1];
        for (var index = 0; index < _workerCount; index++)
        {
            workers[index] = UdpLoopAsync(cancellationToken);
        }

        workers[_workerCount] = _acceptLoop.RunAsync(socket => HandleTcpConnectionAsync(socket, cancellationToken), cancellationToken);
        await Task.WhenAll(workers).ConfigureAwait(false);
        await _acceptLoop.DrainAsync().ConfigureAwait(false);
    }

    internal void WriteTotals(Utf8JsonWriter writer, DnsTotalsKeys keys)
    {
        writer.WriteNumber(keys.Port, _port);
        writer.WriteNumber(keys.UdpQueries, _udpQueries);
        writer.WriteNumber(keys.UdpAnswers, _udpAnswers);
        writer.WriteNumber(keys.UdpEmptyAnswers, _udpEmpty);
        writer.WriteNumber(keys.UdpMalformed, _udpMalformed);
        writer.WriteNumber(keys.UdpSendErrors, _udpSendErrors);
        writer.WriteNumber(keys.TcpQueries, _tcpQueries);
        writer.WriteNumber(keys.TcpAnswers, _tcpAnswers);
        writer.WriteNumber(keys.TcpEmptyAnswers, _tcpEmpty);
        writer.WriteNumber(keys.TcpMalformed, _tcpMalformed);
        writer.WriteNumber(keys.TcpTruncatedFrames, _tcpTruncated);
        writer.WriteNumber(keys.TcpConnections, _tcpConnectionsAccepted);
        writer.WriteNumber(keys.TcpAcceptErrors, _acceptLoop.AcceptErrors);
        writer.WriteNumber(keys.TcpAborted, _tcpAborted);
    }

    internal async ValueTask WriteSummaryAsync(CancellationToken cancellationToken)
    {
        await _ledger.WriteAsync(
            writer =>
            {
                writer.WriteString(ArmKeys.Common.Record.Type, "dnsSummary");
                WriteTotals(writer, DnsTotalsKeys.Summary);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static int BuildAnswer(ReadOnlySpan<byte> message, Span<byte> response, out int answerCount)
    {
        if (!DnsWire.TryParseQuery(message, out var query))
        {
            answerCount = 0;
            return -1;
        }

        return DnsWire.BuildResponse(message, query, response, out answerCount);
    }

    private async Task UdpLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxMessageLength];
        var response = new byte[MaxMessageLength];

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, _sourceTemplate, cancellationToken).ConfigureAwait(false);
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

            if (!await AnswerAsync(result, buffer, response, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async ValueTask<bool> AnswerAsync(SocketReceiveFromResult result, byte[] buffer, byte[] response, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _udpQueries);
        var responseLength = BuildAnswer(buffer.AsSpan(0, result.ReceivedBytes), response, out var answerCount);
        if (responseLength <= 0)
        {
            Interlocked.Increment(ref _udpMalformed);
            return true;
        }

        if (answerCount > 0)
        {
            Interlocked.Increment(ref _udpAnswers);
        }
        else
        {
            Interlocked.Increment(ref _udpEmpty);
        }

        try
        {
            await _udp.SendToAsync(response.AsMemory(0, responseLength), SocketFlags.None, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (SocketException)
        {
            Interlocked.Increment(ref _udpSendErrors);
            return true;
        }
    }

    private async ValueTask<bool> AnswerTcpAsync(Socket socket, byte[] message, byte[] response, byte[] lengthBuffer, CancellationToken cancellationToken)
    {
        var responseLength = BuildAnswer(message, response, out var answerCount);
        if (responseLength <= 0)
        {
            Interlocked.Increment(ref _tcpMalformed);
            return true;
        }

        if (answerCount > 0)
        {
            Interlocked.Increment(ref _tcpAnswers);
        }
        else
        {
            Interlocked.Increment(ref _tcpEmpty);
        }

        BinaryPrimitives.WriteUInt16BigEndian(lengthBuffer, (ushort)responseLength);
        // A send the peer refused is not this loop's decision: the answer is dropped, and the next
        // read sees the end of the stream, which is where the connection ends.
        _ = await SocketIo.TrySendAllAsync(socket, lengthBuffer, cancellationToken).ConfigureAwait(false);
        _ = await SocketIo.TrySendAllAsync(socket, response.AsMemory(0, responseLength), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>'s length and books where the stream ended when it did
    /// not. A peer that stops inside the length prefix or inside the message leaves one message cut in
    /// half, which is this listener's own truncated frame; a peer that stops between messages ended
    /// its stream cleanly and is not counted (D19.3 C).
    /// </summary>
    private async ValueTask<bool> ReadExactAsync(Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var outcome = await SocketIo.ReadExactAsync(socket, buffer, cancellationToken).ConfigureAwait(false);
        if (outcome == ReadExactOutcome.Short)
        {
            Interlocked.Increment(ref _tcpTruncated);
        }

        return outcome == ReadExactOutcome.Complete;
    }

    private async Task HandleTcpConnectionAsync(Socket socket, CancellationToken cancellationToken)
    {
        using (socket)
        {
            Interlocked.Increment(ref _tcpConnectionsAccepted);
            var lengthBuffer = new byte[2];
            var response = new byte[MaxMessageLength];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(socket, lengthBuffer, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);
                    Interlocked.Increment(ref _tcpQueries);
                    if (length is 0 or > MaxMessageLength)
                    {
                        Interlocked.Increment(ref _tcpMalformed);
                        return;
                    }

                    var message = new byte[length];
                    if (!await ReadExactAsync(socket, message, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    if (!await AnswerTcpAsync(socket, message, response, lengthBuffer, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _tcpAborted);
            }
            catch (SocketException)
            {
                Interlocked.Increment(ref _tcpAborted);
            }
            catch (ObjectDisposedException)
            {
                Interlocked.Increment(ref _tcpAborted);
            }
        }
    }
}
