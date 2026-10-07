using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Target;

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
    private long _tcpConnectionsAccepted;
    private long _tcpAborted;

    internal DnsServer(IPEndPoint endPoint, JsonlSink ledger, int workerCount)
    {
        _ledger = ledger;
        _workerCount = workerCount;
        _port = endPoint.Port;
        _sourceTemplate = Sockets.SourceTemplate(endPoint);

        _udp = Sockets.BindUdp(endPoint);
        _acceptLoop = new TcpAcceptLoop(Sockets.BindTcpListener(endPoint));
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

    internal void WriteTotals(Utf8JsonWriter writer)
    {
        writer.WriteNumber("port", _port);
        writer.WriteNumber("udpQueries", _udpQueries);
        writer.WriteNumber("udpAnswers", _udpAnswers);
        writer.WriteNumber("udpEmptyAnswers", _udpEmpty);
        writer.WriteNumber("udpMalformed", _udpMalformed);
        writer.WriteNumber("udpSendErrors", _udpSendErrors);
        writer.WriteNumber("tcpQueries", _tcpQueries);
        writer.WriteNumber("tcpAnswers", _tcpAnswers);
        writer.WriteNumber("tcpEmptyAnswers", _tcpEmpty);
        writer.WriteNumber("tcpMalformed", _tcpMalformed);
        writer.WriteNumber("tcpConnections", _tcpConnectionsAccepted);
        writer.WriteNumber("tcpAborted", _tcpAborted);
    }

    internal async ValueTask WriteSummaryAsync(CancellationToken cancellationToken)
    {
        await _ledger.WriteAsync(
            writer =>
            {
                writer.WriteString("type", "dnsSummary");
                WriteTotals(writer);
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
                    if (!await SocketIo.ReadExactAsync(socket, lengthBuffer, cancellationToken).ConfigureAwait(false))
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
                    if (!await SocketIo.ReadExactAsync(socket, message, cancellationToken).ConfigureAwait(false))
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
