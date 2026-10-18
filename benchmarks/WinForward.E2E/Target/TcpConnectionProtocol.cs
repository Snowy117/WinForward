using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Target;

[StructLayout(LayoutKind.Auto)]
internal readonly struct TcpModeOutcome
{
    internal TcpModeOutcome(TcpVerdict verdict, long bytesEchoed, int protocolErrors, bool truncated)
    {
        Verdict = verdict;
        BytesEchoed = bytesEchoed;
        ProtocolErrors = protocolErrors;
        Truncated = truncated;
    }

    internal TcpVerdict Verdict { get; }

    internal long BytesEchoed { get; }

    internal int ProtocolErrors { get; }

    /// <summary>
    /// Whether the peer's close landed inside a frame, leaving bytes that never reached a boundary.
    /// The ledger counts these apart from the clean closes that share <see cref="Verdict"/>.
    /// </summary>
    internal bool Truncated { get; }
}

[StructLayout(LayoutKind.Auto)]
internal readonly struct CommandOutcome
{
    internal CommandOutcome(TcpMode mode, uint expectedBytes, bool modeKnown, TcpModeOutcome? outcome)
    {
        Mode = mode;
        ExpectedBytes = expectedBytes;
        ModeKnown = modeKnown;
        Outcome = outcome;
    }

    /// <summary>
    /// A connection the socket's own teardown ended before the protocol could measure anything: an
    /// outcome that is published nowhere, because teardown is not a data point.
    /// </summary>
    internal static CommandOutcome TornDown { get; } = new(TcpMode.Clean, 0, modeKnown: false, outcome: null);

    internal TcpMode Mode { get; }

    internal uint ExpectedBytes { get; }

    internal bool ModeKnown { get; }

    /// <summary>
    /// What the connection was measured to be, or <see langword="null"/> when teardown ended it
    /// before there was anything to measure: the two are different answers and only one of them is
    /// published.
    /// </summary>
    internal TcpModeOutcome? Outcome { get; }
}

/// <summary>
/// One TCP connection's command/echo state machine, from the first frame to the verdict its mode
/// asks for. It reads and writes the socket and nothing else: the ledger record for the connection
/// is the server's business.
/// </summary>
internal static class TcpConnectionProtocol
{
    private static readonly TimeSpan s_stallDelay = TimeSpan.FromSeconds(2);

    internal static async ValueTask<CommandOutcome> RunConnectionAsync(
        Socket socket,
        FrameStreamReader reader,
        CancellationToken cancellationToken,
        TimeSpan responseDelay = default)
    {
        try
        {
            var first = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (first)
            {
                case FrameReadStatus.EndOfStream:
                    return new CommandOutcome(TcpMode.Clean, 0, modeKnown: false, new TcpModeOutcome(TcpVerdict.ClientClosedEarly, 0, 0, truncated: false));
                case FrameReadStatus.Truncated:
                    // Closed inside the command frame: there is no mode to run and no boundary to read
                    // one from, so this is a protocol error like any other unreadable frame -- with
                    // the truncation recorded beside it.
                    return new CommandOutcome(TcpMode.Clean, 0, modeKnown: false, new TcpModeOutcome(TcpVerdict.ProtocolError, 0, 1, truncated: true));
                case FrameReadStatus.Frame
                    when reader.Header.Sequence == FrameCodec.CommandSequence
                    && TcpCommand.TryParse(reader.Payload.Span, out var mode, out var expectedBytes):
                    var outcome = await RunModeAsync(socket, reader, mode, expectedBytes, responseDelay, cancellationToken).ConfigureAwait(false);
                    return new CommandOutcome(mode, expectedBytes, modeKnown: true, outcome);
                case FrameReadStatus.Frame:
                case FrameReadStatus.BadMagic:
                case FrameReadStatus.BadLength:
                case FrameReadStatus.BadChecksum:
                default:
                    return new CommandOutcome(TcpMode.Clean, 0, modeKnown: false, new TcpModeOutcome(TcpVerdict.ProtocolError, 0, 1, truncated: false));
            }
        }
        catch (OperationCanceledException)
        {
            return new CommandOutcome(TcpMode.Clean, 0, modeKnown: false, new TcpModeOutcome(TcpVerdict.Error, 0, 0, truncated: false));
        }
        catch (SocketException)
        {
            return new CommandOutcome(TcpMode.Clean, 0, modeKnown: false, new TcpModeOutcome(TcpVerdict.Error, 0, 0, truncated: false));
        }
        catch (IOException)
        {
            return new CommandOutcome(TcpMode.Clean, 0, modeKnown: false, new TcpModeOutcome(TcpVerdict.Error, 0, 0, truncated: false));
        }
        catch (ObjectDisposedException)
        {
            // Teardown closed the socket under the connection: nothing about this peer was measured,
            // so the connection publishes no verdict.
            return CommandOutcome.TornDown;
        }
    }

    private static async Task<TcpModeOutcome> RunModeAsync(
        Socket socket,
        FrameStreamReader reader,
        TcpMode mode,
        uint expectedBytes,
        TimeSpan responseDelay,
        CancellationToken cancellationToken)
    {
        var delayed = responseDelay > TimeSpan.Zero
            ? new DelayedStreamWrites(socket, responseDelay, cancellationToken)
            : null;

        try
        {
            return await RunModeLoopAsync(socket, reader, mode, expectedBytes, delayed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // An exit the read path decided (a socket fault or teardown) still owns a queue; observing
            // it keeps a write that failed for the same reason from becoming an unobserved task.
            if (delayed is not null)
            {
                await delayed.ObserveAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<TcpModeOutcome> RunModeLoopAsync(
        Socket socket,
        FrameStreamReader reader,
        TcpMode mode,
        uint expectedBytes,
        DelayedStreamWrites? delayed,
        CancellationToken cancellationToken)
    {
        var connectionId = reader.Header.ConnectionId;
        long bytesEchoed = 0;
        const int protocolErrors = 0;
        var stallPending = mode == TcpMode.Stall;

        while (true)
        {
            var status = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            // A close, a truncated frame and a malformed one all end the connection here; the helper
            // keeps the close path's trailer or FIN behind the echo queue.
            if (status is not FrameReadStatus.Frame)
            {
                return await FinishReadAsync(socket, connectionId, status, mode, bytesEchoed, protocolErrors, delayed, cancellationToken).ConfigureAwait(false);
            }

            if (stallPending)
            {
                stallPending = false;

                // The stall is the injected fault the client is measuring, so it has to run to its
                // full length: forwarding the shutdown token would let a teardown cut it short and
                // record Error where the mode asked for Stall.
                await Task.Delay(s_stallDelay, CancellationToken.None).ConfigureAwait(false);
            }

            var (echoed, outcome) = await EchoFrameAsync(socket, reader, mode, expectedBytes, bytesEchoed, protocolErrors, delayed, cancellationToken).ConfigureAwait(false);
            bytesEchoed = echoed;
            if (outcome is { } completed)
            {
                return completed;
            }
        }
    }

    private static async Task<TcpModeOutcome> FinishReadAsync(
        Socket socket,
        uint connectionId,
        FrameReadStatus status,
        TcpMode mode,
        long bytesEchoed,
        int protocolErrors,
        DelayedStreamWrites? delayed,
        CancellationToken cancellationToken)
    {
        // A queued echo must reach the wire before the close path's trailer or FIN: the delay belongs
        // to the response, not to the connection's ending.
        if (delayed is not null)
        {
            await delayed.CompleteAsync().ConfigureAwait(false);
        }

        if (status is FrameReadStatus.EndOfStream)
        {
            return await CompleteOnEndOfStreamAsync(socket, connectionId, mode, bytesEchoed, protocolErrors, cancellationToken).ConfigureAwait(false);
        }

        var errors = protocolErrors + 1;
        return status is FrameReadStatus.Truncated
            ? new TcpModeOutcome(TcpVerdict.ProtocolError, bytesEchoed, errors, truncated: true)
            : new TcpModeOutcome(TcpVerdict.ProtocolError, bytesEchoed, errors, truncated: false);
    }

    private static async Task<(long BytesEchoed, TcpModeOutcome? Outcome)> EchoFrameAsync(
        Socket socket,
        FrameStreamReader reader,
        TcpMode mode,
        uint expectedBytes,
        long bytesEchoed,
        int protocolErrors,
        DelayedStreamWrites? delayed,
        CancellationToken cancellationToken)
    {
        if (delayed is not null)
        {
            // The queue copies the frame because the reader reuses its buffer on the next read.
            _ = delayed.EnqueueAsync(reader.Raw);
        }
        else if (!await SocketIO.TrySendAllAsync(socket, reader.Raw, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The peer closed while a frame was being echoed.");
        }

        var echoed = bytesEchoed + reader.Payload.Length;
        if ((mode is TcpMode.ResetAfterN or TcpMode.PartialFin) && echoed >= expectedBytes)
        {
            // The reset or half-close is the mode's verdict and has to follow the echo bytes it was
            // measured at, not overtake a response still waiting on its delay.
            if (delayed is not null)
            {
                await delayed.CompleteAsync().ConfigureAwait(false);
            }

            if (mode == TcpMode.ResetAfterN)
            {
                socket.LingerState = new LingerOption(enable: true, 0);
                return (echoed, new TcpModeOutcome(TcpVerdict.Reset, echoed, protocolErrors, truncated: false));
            }

            socket.Shutdown(SocketShutdown.Send);
            return (echoed, new TcpModeOutcome(TcpVerdict.PartialFin, echoed, protocolErrors, truncated: false));
        }

        return (echoed, null);
    }

    /// <summary>
    /// The verdict for a peer that closed its send side first. Each mode's contract says what that
    /// close means, and a mode this target cannot read is a harness defect rather than a clean run.
    /// </summary>
    private static async Task<TcpModeOutcome> CompleteOnEndOfStreamAsync(
        Socket socket,
        uint connectionId,
        TcpMode mode,
        long bytesEchoed,
        int protocolErrors,
        CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case TcpMode.HalfClose:
                await SendTrailerAsync(socket, connectionId, cancellationToken).ConfigureAwait(false);
                socket.Shutdown(SocketShutdown.Send);
                return new TcpModeOutcome(TcpVerdict.HalfClose, bytesEchoed, protocolErrors, truncated: false);
            case TcpMode.ResetAfterN or TcpMode.PartialFin:
                return new TcpModeOutcome(TcpVerdict.ClientClosedEarly, bytesEchoed, protocolErrors, truncated: false);
            case TcpMode.Clean or TcpMode.Stall:
                socket.Shutdown(SocketShutdown.Send);
                return new TcpModeOutcome(mode == TcpMode.Stall ? TcpVerdict.Stall : TcpVerdict.Clean, bytesEchoed, protocolErrors, truncated: false);
            default:
                throw new InvalidOperationException($"the connection ended in an unclassified mode: {mode}");
        }
    }

    private static async ValueTask SendTrailerAsync(Socket socket, uint connectionId, CancellationToken cancellationToken)
    {
        const int frameLength = FrameCodec.HeaderSize + TrailerProtocol.PayloadBytes + FrameCodec.TrailerSize;
        var buffer = new byte[frameLength * TrailerProtocol.FrameCount];
        for (var index = 0; index < TrailerProtocol.FrameCount; index++)
        {
            var frame = buffer.AsSpan(index * frameLength, frameLength);
            var sequence = TrailerProtocol.SequenceBase + (ulong)index;
            var payload = frame.Slice(FrameCodec.HeaderSize, TrailerProtocol.PayloadBytes);
            Filler.Fill(connectionId, sequence, payload);
            FrameCodec.WriteHeader(frame, connectionId, sequence, (ulong)Stopwatch.GetTimestamp(), TrailerProtocol.PayloadBytes);
            FrameCodec.FinishFrame(frame, TrailerProtocol.PayloadBytes);
        }

        if (!await SocketIO.TrySendAllAsync(socket, buffer, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The peer closed while a frame was being echoed.");
        }
    }
}
