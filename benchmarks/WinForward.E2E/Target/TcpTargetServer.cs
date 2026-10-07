using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinForward.E2E.Contracts.Json;
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

internal sealed class TcpTargetServer : IAsyncDisposable
{
    // Sized from the enum, never from a literal: WriteTotals indexes the tally array by verdict, so
    // a ninth verdict would throw inside a ledger record. The sink books that as one failed record
    // instead of taking the target down, but the summary would still be missing from the ledger,
    // which is why the sizing is the fix and the call-site guard is only the backstop.
    private static readonly int s_verdictCount = Enum.GetValues<TcpVerdict>().Length;
    private static readonly TimeSpan s_stallDelay = TimeSpan.FromSeconds(2);

    private readonly Socket _listener;
    private readonly JsonlSink _ledger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _connections = [];
    private readonly long[] _verdicts = new long[s_verdictCount];
    private long _connectionCount;
    private long _bytesEchoed;
    private long _protocolErrors;

    internal TcpTargetServer(EndPoint endPoint, JsonlSink ledger)
    {
        _ledger = ledger;
        _listener = Sockets.BindTcpListener(endPoint);
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            socket.NoDelay = true;
            _connections.Add(HandleConnectionAsync(socket));
            if (_connections.Count >= 256)
            {
                _connections.RemoveAll(static task => task.IsCompleted);
            }
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_connections).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _listener.Dispose();
        _shutdown.Dispose();
        return ValueTask.CompletedTask;
    }

    internal void WriteTotals(Utf8JsonWriter writer)
    {
        writer.WriteNumber("connections", Interlocked.Read(ref _connectionCount));
        writer.WriteNumber("bytesEchoed", Interlocked.Read(ref _bytesEchoed));
        writer.WriteNumber("protocolErrors", Interlocked.Read(ref _protocolErrors));
        writer.WriteStartObject("verdicts");
        foreach (var verdict in Enum.GetValues<TcpVerdict>())
        {
            writer.WriteNumber(TcpCommand.Name(verdict), Volatile.Read(ref _verdicts[(int)verdict]));
        }

        writer.WriteEndObject();
    }

    internal async ValueTask WriteSummaryAsync(CancellationToken cancellationToken)
    {
        await _ledger.WriteAsync(
            writer =>
            {
                writer.WriteString("type", "tcpSummary");
                WriteTotals(writer);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SendAllAsync(Socket socket, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        while (!data.IsEmpty)
        {
            var sent = await socket.SendAsync(data, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (sent <= 0)
            {
                throw new IOException("The peer closed while a frame was being echoed.");
            }

            data = data[sent..];
        }
    }

    private static string RemoteEndPointText(Socket socket)
    {
        try
        {
            return socket.RemoteEndPoint is IPEndPoint remote
                ? string.Create(CultureInfo.InvariantCulture, $"{remote.Address}:{remote.Port}")
                : "unknown";
        }
        catch (SocketException)
        {
            return "unknown";
        }
        catch (ObjectDisposedException)
        {
            return "unknown";
        }
    }

    private async Task HandleConnectionAsync(Socket socket)
    {
        using (socket)
        {
            var startedTicks = Stopwatch.GetTimestamp();
            var peer = RemoteEndPointText(socket);
            Interlocked.Increment(ref _connectionCount);

            var reader = new FrameStreamReader(socket);
            var command = await RunConnectionAsync(socket, reader, _shutdown.Token).ConfigureAwait(false);

            var endedTicks = Stopwatch.GetTimestamp();
            Interlocked.Add(ref _bytesEchoed, command.Outcome.BytesEchoed);
            Interlocked.Add(ref _protocolErrors, command.Outcome.ProtocolErrors);
            Interlocked.Increment(ref _verdicts[(int)command.Outcome.Verdict]);

            // The ledger's policy swallows and counts an I/O failure on its own; this guard is what
            // covers a body the sink was asked to propagate, and it keeps the fire-and-forget task
            // from faulting into the shutdown join. The connection is reset so an unrecorded
            // attempt is visible to its peer rather than indistinguishable from a recorded one, and
            // the listener keeps serving (D14.7 item 1).
            try
            {
                await WriteConnectionAsync(reader.Header.ConnectionId, command, peer, startedTicks, endedTicks).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await TargetLog.ReportAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"e2e target: connection {reader.Header.ConnectionId} could not be recorded ({_ledger.WriteErrors} ledger write error(s) so far): {exception.GetType().Name}: {exception.Message}")).ConfigureAwait(false);
                socket.LingerState = new LingerOption(enable: true, 0);
            }
        }
    }

    private async ValueTask WriteConnectionAsync(uint connectionId, CommandOutcome command, string peer, long startedTicks, long endedTicks)
    {
        var modeText = command.ModeKnown ? TcpCommand.Name(command.Mode) : "unknown";
        var verdictText = TcpCommand.Name(command.Outcome.Verdict);
        var bytesEchoed = command.Outcome.BytesEchoed;
        await _ledger.WriteAsync(
            writer =>
            {
                writer.WriteString("type", "tcp");
                writer.WriteNumber("connectionId", connectionId);
                writer.WriteString("mode", modeText);
                writer.WriteNumber("expectedBytes", command.ExpectedBytes);
                writer.WriteNumber("bytesEchoed", bytesEchoed);
                writer.WriteString("verdict", verdictText);
                writer.WriteString("peer", peer);
                writer.WriteNumber("startedTicks", startedTicks);
                writer.WriteNumber("endedTicks", endedTicks);
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async ValueTask<CommandOutcome> RunConnectionAsync(Socket socket, FrameStreamReader reader, CancellationToken cancellationToken)
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

            await SendAllAsync(socket, reader.Raw, cancellationToken).ConfigureAwait(false);
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

        await SendAllAsync(socket, buffer, cancellationToken).ConfigureAwait(false);
    }
}
