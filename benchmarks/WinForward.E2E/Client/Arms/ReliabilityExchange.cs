using System.Net.Sockets;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal static class ReliabilityExchange
{
    private const int AttemptTimeoutMilliseconds = 10_000;

    private static ReliabilityOutcome ExpectedOutcome(TcpMode mode) => mode switch
    {
        TcpMode.ResetAfterN => ReliabilityOutcome.Reset,
        TcpMode.PartialFin => ReliabilityOutcome.UnexpectedEof,
        _ => ReliabilityOutcome.Clean,
    };

    internal static async Task<ReliabilityAttempt> RunAttemptAsync(
        ArmContext context,
        TcpMode mode,
        int expectedBytes,
        uint connectionId,
        long intendedTicks,
        CancellationToken cancellationToken)
    {
        var attempt = new ReliabilityAttempt { Mode = mode, Expected = ExpectedOutcome(mode), ConnectionId = connectionId };
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCancellation.CancelAfter(AttemptTimeoutMilliseconds);
        var token = attemptCancellation.Token;
        using var socket = context.CreateTcpSocket();

        var result = await ExchangeAsync(context, socket, attempt, mode, expectedBytes, connectionId, intendedTicks, token, cancellationToken).ConfigureAwait(false);
        attempt.Status = result._status;
        attempt.Reset = result._reset;
        switch (result._status)
        {
            case ExchangeStatus.ConnectFail:
                attempt.Observed = ReliabilityOutcome.ConnectFail;
                return attempt;
            case ExchangeStatus.Cancelled:
                return attempt;
            case ExchangeStatus.Timeout:
                attempt.Observed = ReliabilityOutcome.Timeout;
                return attempt;
            case ExchangeStatus.Completed:
                // A reset after N bytes legitimately destroys whatever echo was still buffered, so only
                // modes whose contract is a complete echo can be truncated.
                attempt.Truncated = mode != TcpMode.ResetAfterN && attempt.Echoed < expectedBytes;
                attempt.Observed = Classify(mode, attempt.Reset, attempt.Eof, attempt.ProtocolError, attempt.OtherError, attempt.Echoed, attempt.TrailerBytes, expectedBytes);
                return attempt;
            default:
                // A status this arm has no classification for is a harness defect, not a smaller
                // measurement: a completed-looking attempt would hide the new mode from every gate.
                throw new InvalidOperationException($"the exchange ended with an unclassified status: {result._status}");
        }
    }

    private static async ValueTask<ExchangeResult> ExchangeAsync(
        ArmContext context,
        Socket socket,
        ReliabilityAttempt attempt,
        TcpMode mode,
        int expectedBytes,
        uint connectionId,
        long intendedTicks,
        CancellationToken token,
        CancellationToken cancellationToken)
    {
        var sessionStart = Clock.Now;
        var result = new ExchangeResult { _status = ExchangeStatus.Completed };

        try
        {
            if (!(await SocketOps.TryConnectAsync(socket, context.TcpEndPoint, token).ConfigureAwait(false)).Ok)
            {
                result._status = ExchangeStatus.ConnectFail;
                return result;
            }

            attempt.ConnectTicks = Clock.Now - sessionStart;
            context.Latency.TcpConnect.Record(Clock.ToNanoseconds(Clock.Now - intendedTicks));

            // The transfer is the request send on its own: sessionStart predates the connect, so
            // timing from it would publish connect + send under a name that promises a transfer.
            var sendStart = Clock.Now;
            await SendRequestAsync(socket, connectionId, mode, expectedBytes, token).ConfigureAwait(false);
            attempt.TransferTicks = Clock.Now - sendStart;
            attempt.TransferMeasured = true;

            if (mode is TcpMode.Clean or TcpMode.HalfClose or TcpMode.Stall)
            {
                SocketOps.ShutdownQuietly(socket, SocketShutdown.Send);
            }

            // Counted into the attempt as each frame arrives, so a receive that ends in a reset or a
            // timeout still carries what had already been observed.
            await ReceivePhaseAsync(socket, expectedBytes, attempt, token).ConfigureAwait(false);
            return result;
        }
        catch (SocketException exception)
        {
            if (exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                result._reset = true;
            }
            else
            {
                attempt.OtherError = true;
            }
        }
        catch (OperationCanceledException)
        {
            result._status = cancellationToken.IsCancellationRequested ? ExchangeStatus.Cancelled : ExchangeStatus.Timeout;
        }
        catch (ObjectDisposedException)
        {
            // Teardown closed the socket under the attempt: the arm ended before the attempt observed
            // anything, which is the cancelled status -- never an error the peer was seen to produce
            // (D19.2 ⑨).
            result._status = ExchangeStatus.Cancelled;
        }
        catch (IOException)
        {
            attempt.OtherError = true;
        }

        return result;
    }

    private static async ValueTask ReceivePhaseAsync(Socket socket, int expectedBytes, ReliabilityAttempt attempt, CancellationToken cancellationToken)
    {
        var reader = new FrameStreamReader(socket, 32 * 1024);
        while (true)
        {
            var status = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (status)
            {
                case FrameReadStatus.EndOfStream:
                    attempt.Eof = true;
                    return;
                case FrameReadStatus.BadChecksum:
                    attempt.ProtocolError = true;
                    continue;
                case FrameReadStatus.BadMagic:
                case FrameReadStatus.BadLength:
                    attempt.ProtocolError = true;
                    return;
                case FrameReadStatus.Truncated:
                    // The peer closed inside a frame: the boundary is gone, so this attempt's stream
                    // is unreadable from here on and it is booked as what it is. EndOfStream is the
                    // status that says the peer closed between frames, and this one deliberately does
                    // not set that flag -- the two are different observations about the same close.
                    attempt.ProtocolError = true;
                    return;
                case FrameReadStatus.Frame:
                    if (attempt.Echoed < expectedBytes)
                    {
                        attempt.Echoed += reader.Payload.Length;
                    }
                    else
                    {
                        attempt.TrailerBytes += reader.Payload.Length;
                    }

                    continue;
                default:
                    // An unclassified status is booked as a protocol error for this attempt rather
                    // than absorbed as a good frame.
                    attempt.ProtocolError = true;
                    return;
            }
        }
    }

    private static ReliabilityOutcome Classify(
        TcpMode mode,
        bool reset,
        bool eof,
        bool protocolError,
        bool otherError,
        long echoed,
        long trailerBytes,
        long expectedBytes)
    {
        if (otherError && !reset && !eof)
        {
            return ReliabilityOutcome.OtherError;
        }

        if (reset)
        {
            return mode == TcpMode.HalfClose ? ReliabilityOutcome.HalfCloseViolation : ReliabilityOutcome.Reset;
        }

        if (protocolError && !eof)
        {
            return ReliabilityOutcome.OtherError;
        }

        if (!eof)
        {
            return ReliabilityOutcome.Timeout;
        }

        return mode switch
        {
            TcpMode.HalfClose => echoed >= expectedBytes && trailerBytes >= TrailerProtocol.TotalBytes
                ? ReliabilityOutcome.Clean
                : ReliabilityOutcome.HalfCloseViolation,
            TcpMode.PartialFin => ReliabilityOutcome.UnexpectedEof,
            _ => echoed >= expectedBytes ? ReliabilityOutcome.Clean : ReliabilityOutcome.UnexpectedEof,
        };
    }

    private static async ValueTask SendRequestAsync(Socket socket, uint connectionId, TcpMode mode, int expectedBytes, CancellationToken cancellationToken)
    {
        await SocketOps.SendCommandAsync(socket, connectionId, mode, (uint)expectedBytes, cancellationToken).ConfigureAwait(false);

        var frame = new FrameBuffer(ReliabilityArm.FramePayloadBytes);
        var sent = 0;
        ulong sequence = 0;
        while (sent < expectedBytes)
        {
            var chunk = Math.Min(ReliabilityArm.FramePayloadBytes, expectedBytes - sent);
            sequence++;
            var length = frame.Build(connectionId, sequence, Clock.Now, chunk);
            await socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken).ConfigureAwait(false);
            sent += chunk;
        }
    }
}
