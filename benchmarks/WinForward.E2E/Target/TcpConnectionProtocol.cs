using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Target;

[StructLayout(LayoutKind.Auto)]
internal readonly struct TcpModeOutcome
{
    internal TcpModeOutcome(TcpVerdict verdict, long bytesEchoed, int protocolErrors)
    {
        Verdict = verdict;
        BytesEchoed = bytesEchoed;
        ProtocolErrors = protocolErrors;
    }

    internal TcpVerdict Verdict { get; }

    internal long BytesEchoed { get; }

    internal int ProtocolErrors { get; }
}

[StructLayout(LayoutKind.Auto)]
internal readonly struct CommandOutcome
{
    internal CommandOutcome(TcpMode mode, uint expectedBytes, bool modeKnown, TcpModeOutcome outcome)
    {
        Mode = mode;
        ExpectedBytes = expectedBytes;
        ModeKnown = modeKnown;
        Outcome = outcome;
    }

    internal TcpMode Mode { get; }

    internal uint ExpectedBytes { get; }

    internal bool ModeKnown { get; }

    internal TcpModeOutcome Outcome { get; }
}

/// <summary>
/// One TCP connection's command/echo state machine, from the first frame to the verdict its mode
/// asks for. It reads and writes the socket and nothing else: the ledger record for the connection
/// is the server's business.
/// </summary>
internal static class TcpConnectionProtocol
{
    private static readonly TimeSpan s_stallDelay = TimeSpan.FromSeconds(2);

    internal static async ValueTask<CommandOutcome> RunConnectionAsync(Socket socket, FrameStreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            var first = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (first)
            {
                case FrameReadStatus.EndOfStream:
                    return new CommandOutcome(TcpMode.Clean, 0, modeKnown: false, new TcpModeOutcome(TcpVerdict.ClientClosedEarly, 0, 0));
                case FrameReadStatus.Frame
                    when reader.Header.Sequence == FrameCodec.CommandSequence
                    && TcpCommand.TryParse(reader.Payload.Span, out var mode, out var expectedBytes):
                    var outcome = await RunModeAsync(socket, reader, mode, expectedBytes, cancellationToken).ConfigureAwait(false);
                    return new CommandOutcome(mode, expectedBytes, modeKnown: true, outcome);
                case FrameReadStatus.Frame:
                case FrameReadStatus.BadMagic:
                case FrameReadStatus.BadLength:
                case FrameReadStatus.BadChecksum:
                default:
                    return new CommandOutcome(TcpMode.Clean, 0, modeKnown: false, new TcpModeOutcome(TcpVerdict.ProtocolError, 0, 1));
            }
        }
        catch (OperationCanceledException)
        {
            return new CommandOutcome(TcpMode.Clean, 0, modeKnown: false, new TcpModeOutcome(TcpVerdict.Error, 0, 0));
        }
        catch (SocketException)
        {
            return new CommandOutcome(TcpMode.Clean, 0, modeKnown: false, new TcpModeOutcome(TcpVerdict.Error, 0, 0));
        }
        catch (IOException)
        {
            return new CommandOutcome(TcpMode.Clean, 0, modeKnown: false, new TcpModeOutcome(TcpVerdict.Error, 0, 0));
        }
        catch (ObjectDisposedException)
        {
            return new CommandOutcome(TcpMode.Clean, 0, modeKnown: false, new TcpModeOutcome(TcpVerdict.Error, 0, 0));
        }
    }

    private static async Task<TcpModeOutcome> RunModeAsync(Socket socket, FrameStreamReader reader, TcpMode mode, uint expectedBytes, CancellationToken cancellationToken)
    {
        var connectionId = reader.Header.ConnectionId;
        long bytesEchoed = 0;
        var protocolErrors = 0;
        var stallPending = mode == TcpMode.Stall;

        while (true)
        {
            var status = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (status == FrameReadStatus.EndOfStream)
            {
                return await CompleteOnEndOfStreamAsync(socket, connectionId, mode, bytesEchoed, protocolErrors, cancellationToken).ConfigureAwait(false);
            }

            if (status != FrameReadStatus.Frame)
            {
                protocolErrors++;
                return new TcpModeOutcome(TcpVerdict.ProtocolError, bytesEchoed, protocolErrors);
            }

            if (stallPending)
            {
                stallPending = false;

                // The stall is the injected fault the client is measuring, so it has to run to its
                // full length: forwarding the shutdown token would let a teardown cut it short and
                // record Error where the mode asked for Stall.
                await Task.Delay(s_stallDelay, CancellationToken.None).ConfigureAwait(false);
            }

            if (!await SocketIo.TrySendAllAsync(socket, reader.Raw, cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("The peer closed while a frame was being echoed.");
            }

            bytesEchoed += reader.Payload.Length;

            if ((mode is TcpMode.ResetAfterN or TcpMode.PartialFin) && bytesEchoed >= expectedBytes)
            {
                if (mode == TcpMode.ResetAfterN)
                {
                    socket.LingerState = new LingerOption(enable: true, 0);
                    return new TcpModeOutcome(TcpVerdict.Reset, bytesEchoed, protocolErrors);
                }

                socket.Shutdown(SocketShutdown.Send);
                return new TcpModeOutcome(TcpVerdict.PartialFin, bytesEchoed, protocolErrors);
            }
        }
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
                return new TcpModeOutcome(TcpVerdict.HalfClose, bytesEchoed, protocolErrors);
            case TcpMode.ResetAfterN or TcpMode.PartialFin:
                return new TcpModeOutcome(TcpVerdict.ClientClosedEarly, bytesEchoed, protocolErrors);
            case TcpMode.Clean or TcpMode.Stall:
                socket.Shutdown(SocketShutdown.Send);
                return new TcpModeOutcome(mode == TcpMode.Stall ? TcpVerdict.Stall : TcpVerdict.Clean, bytesEchoed, protocolErrors);
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

        if (!await SocketIo.TrySendAllAsync(socket, buffer, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The peer closed while a frame was being echoed.");
        }
    }
}
