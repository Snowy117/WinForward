using System.Net.Sockets;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal enum PersistentExchange
{
    Echoed,
    NotSent,
    NotEchoed,
}

internal enum PersistentFrame
{
    Matched,
    Skipped,
    Dead,
}

internal enum PersistentWait
{
    Readable,
    TimedOut,
    Faulted,
}

internal sealed class PersistentLink : IDisposable
{
    internal PersistentLink(Socket socket, FrameStreamReader reader, uint connectionId)
    {
        Socket = socket;
        Reader = reader;
        ConnectionId = connectionId;
    }

    internal Socket Socket { get; }

    internal FrameStreamReader Reader { get; }

    internal uint ConnectionId { get; }

    public void Dispose()
    {
        SocketOps.ShutdownQuietly(Socket, SocketShutdown.Both);
        Socket.Dispose();
    }
}

internal static class PersistentConnection
{
    private const uint ConnectionIdBase = 0x5045_0000u;

    internal static async ValueTask<PersistentExchange> ExchangeAsync(
        ArmContext context,
        PersistentLink link,
        FrameBuffer frame,
        ulong sequence,
        long intendedTicks,
        long roundDeadlineTicks,
        PersistentCounters state,
        bool timedRound,
        CancellationToken cancellationToken)
    {
        var length = frame.Build(link.ConnectionId, sequence, Clock.Now);
        try
        {
            var send = link.Socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken);
            if (!send.IsCompleted)
            {
                state._sendWouldBlock++;
            }

            await send.ConfigureAwait(false);
        }
        catch (SocketException)
        {
            state._sendFailures++;
            return PersistentExchange.NotSent;
        }

        state._lastSendTicks = Clock.Now;
        return await TryReadEchoAsync(context, link, sequence, intendedTicks, timedRound, roundDeadlineTicks, state, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<PersistentExchange> TryReadEchoAsync(
        ArmContext context,
        PersistentLink link,
        ulong sequence,
        long intendedTicks,
        bool timedRound,
        long roundDeadlineTicks,
        PersistentCounters state,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            switch (WaitForReadable(link.Socket, roundDeadlineTicks, cancellationToken))
            {
                case PersistentWait.TimedOut:
                    state._timeouts++;
                    return PersistentExchange.NotEchoed;
                case PersistentWait.Faulted:
                    // A socket that faults while the arm waits is the same evidence as a clean close: the
                    // connection is gone, which is what the idle window is there to expose.
                    state._remoteClosed++;
                    return PersistentExchange.NotEchoed;
                case PersistentWait.Readable:
                    switch (await TryReadFrameAsync(link, sequence, state, cancellationToken).ConfigureAwait(false))
                    {
                        case PersistentFrame.Dead:
                            return PersistentExchange.NotEchoed;
                        case PersistentFrame.Skipped:
                            continue;
                        case PersistentFrame.Matched:
                            state._responses++;
                            if (timedRound)
                            {
                                context.Latency.TcpRtt.Record(Clock.ToNanoseconds(Clock.Now - intendedTicks));
                            }

                            return PersistentExchange.Echoed;
                        default:
                            state._protocolErrors++;
                            return PersistentExchange.NotEchoed;
                    }

                default:
                    state._protocolErrors++;
                    return PersistentExchange.NotEchoed;
            }
        }
    }

    private static async ValueTask<PersistentFrame> TryReadFrameAsync(
        PersistentLink link,
        ulong sequence,
        PersistentCounters state,
        CancellationToken cancellationToken)
    {
        FrameReadStatus status;
        try
        {
            status = await link.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            state._remoteClosed++;
            return PersistentFrame.Dead;
        }

        switch (status)
        {
            case FrameReadStatus.EndOfStream:
                state._remoteClosed++;
                return PersistentFrame.Dead;
            case FrameReadStatus.BadChecksum:
                state._corrupt++;
                return PersistentFrame.Skipped;
            case FrameReadStatus.BadMagic:
            case FrameReadStatus.BadLength:
                state._protocolErrors++;
                return PersistentFrame.Dead;
            case FrameReadStatus.Frame:
                break;
            default:
                state._protocolErrors++;
                return PersistentFrame.Dead;
        }

        if (link.Reader.Header.ConnectionId != link.ConnectionId || link.Reader.Header.Sequence != sequence)
        {
            state._unmatchedReplies++;
            return PersistentFrame.Skipped;
        }

        if (!Filler.Matches(link.ConnectionId, sequence, link.Reader.Payload.Span))
        {
            state._corrupt++;
            return PersistentFrame.Skipped;
        }

        return PersistentFrame.Matched;
    }

    // Poll parks the dedicated arm thread in the kernel. The alternative, a cancellation source armed with
    // CancelAfter for every request, would put an allocation and a timer on the per-request path.
    private static PersistentWait WaitForReadable(Socket socket, long roundDeadlineTicks, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remainingTicks = roundDeadlineTicks - Clock.Now;
        if (remainingTicks <= 0)
        {
            return PersistentWait.TimedOut;
        }

        try
        {
            var microseconds = Math.Min(Clock.ToMicroseconds(remainingTicks), int.MaxValue);
            return socket.Poll((int)microseconds, SelectMode.SelectRead) ? PersistentWait.Readable : PersistentWait.TimedOut;
        }
        catch (SocketException)
        {
            return PersistentWait.Faulted;
        }
    }

    internal static async ValueTask<PersistentLink?> TryOpenLinkAsync(
        ArmContext context,
        PersistentCounters state,
        PersistentPlan plan,
        CancellationToken cancellationToken)
    {
        var socket = context.CreateTcpSocket();
        var begin = Clock.Now;
        if (!(await SocketOps.TryConnectAsync(socket, context.TcpEndPoint, cancellationToken).ConfigureAwait(false)).Ok)
        {
            socket.Dispose();
            state._connectFailures++;
            return null;
        }

        // Every TCP connection the arm opens carries its own id, so a reconnect shows up in the target's
        // ledger as a second connection instead of as more traffic on the first one.
        var connectionId = ConnectionIdBase + (uint)state._connectSamples;
        state._connectSamples++;
        state._connectTicks += Clock.Now - begin;
        try
        {
            await SocketOps.SendCommandAsync(socket, connectionId, TcpMode.Clean, plan.ExpectedBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            state._sendFailures++;
            socket.Dispose();
            return null;
        }

        state._generation++;
        if (state._generation > 1)
        {
            state._reconnects++;
        }

        return new PersistentLink(socket, new FrameStreamReader(socket), connectionId);
    }
}
