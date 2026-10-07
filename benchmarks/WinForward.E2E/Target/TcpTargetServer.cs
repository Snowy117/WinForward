using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Target;

internal sealed class TcpTargetServer : IAsyncDisposable
{
    // Sized from the enum, never from a literal: WriteTotals indexes the tally array by verdict, so
    // a ninth verdict would throw inside a ledger record. The sink books that as one failed record
    // instead of taking the target down, but the summary would still be missing from the ledger,
    // which is why the sizing is the fix and the call-site guard is only the backstop.
    private static readonly int s_verdictCount = Enum.GetValues<TcpVerdict>().Length;

    private readonly JsonlSink _ledger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TcpAcceptLoop _acceptLoop;
    private readonly long[] _verdicts = new long[s_verdictCount];
    private long _connectionCount;
    private long _bytesEchoed;
    private long _protocolErrors;

    internal TcpTargetServer(EndPoint endPoint, JsonlSink ledger)
    {
        _ledger = ledger;
        _acceptLoop = new TcpAcceptLoop(Sockets.BindTcpListener(endPoint));
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        await _acceptLoop.RunAsync(HandleConnectionAsync, cancellationToken).ConfigureAwait(false);

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _acceptLoop.DrainAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _acceptLoop.Dispose();
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
            var command = await TcpConnectionProtocol.RunConnectionAsync(socket, reader, _shutdown.Token).ConfigureAwait(false);

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
}
