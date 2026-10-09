using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Target;

/// <summary>
/// The key names one tcp totals block is written with, in write order. The same counters reach the
/// ledger at two levels -- the <c>tcpSummary</c> record's own root and <c>targetSummary/tcp</c> -- and
/// a leaf written at another level is another constant, so the writer takes the level's set
/// instead of spelling a name at the call site.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct TcpTotalsKeys
{
    private TcpTotalsKeys(string connections, string acceptErrors, string bytesEchoed, string protocolErrors, string truncatedFrames, string verdicts)
    {
        Connections = connections;
        AcceptErrors = acceptErrors;
        BytesEchoed = bytesEchoed;
        ProtocolErrors = protocolErrors;
        TruncatedFrames = truncatedFrames;
        Verdicts = verdicts;
    }

    /// <summary>The key set of the <c>tcpSummary</c> record's own totals.</summary>
    internal static TcpTotalsKeys Summary { get; } = new(
        ArmKeys.Ledger.TcpSummary.Connections,
        ArmKeys.Ledger.TcpSummary.AcceptErrors,
        ArmKeys.Ledger.TcpSummary.BytesEchoed,
        ArmKeys.Ledger.TcpSummary.ProtocolErrors,
        ArmKeys.Ledger.TcpSummary.TruncatedFrames,
        ArmKeys.Ledger.TcpSummary.Verdicts);

    /// <summary>The key set of the same block one level down, under <c>targetSummary/tcp</c>.</summary>
    internal static TcpTotalsKeys Target { get; } = new(
        ArmKeys.Ledger.TargetSummary.TcpTotals.Connections,
        ArmKeys.Ledger.TargetSummary.TcpTotals.AcceptErrors,
        ArmKeys.Ledger.TargetSummary.TcpTotals.BytesEchoed,
        ArmKeys.Ledger.TargetSummary.TcpTotals.ProtocolErrors,
        ArmKeys.Ledger.TargetSummary.TcpTotals.TruncatedFrames,
        ArmKeys.Ledger.TargetSummary.TcpTotals.Verdicts);

    internal string Connections { get; }

    internal string AcceptErrors { get; }

    internal string BytesEchoed { get; }

    internal string ProtocolErrors { get; }

    internal string TruncatedFrames { get; }

    internal string Verdicts { get; }
}

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
    private long _truncatedFrames;

    internal TcpTargetServer(EndPoint endPoint, JsonlSink ledger)
        : this(new TcpAcceptLoop(Sockets.BindTcpListener(endPoint)), ledger)
    {
    }

    /// <summary>
    /// The server over a listener the caller already built. A refused accept cannot be produced from an
    /// endpoint -- the kernel decides when one happens -- so the one socket whose accept always fails
    /// has to arrive from outside, which is how the ledger's own evidence for
    /// <see cref="TcpTotalsKeys.AcceptErrors"/> is driven.
    /// </summary>
    internal TcpTargetServer(TcpAcceptLoop acceptLoop, JsonlSink ledger)
    {
        _ledger = ledger;
        _acceptLoop = acceptLoop;
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

    internal void WriteTotals(Utf8JsonWriter writer, TcpTotalsKeys keys)
    {
        writer.WriteNumber(keys.Connections, Interlocked.Read(ref _connectionCount));
        writer.WriteNumber(keys.AcceptErrors, _acceptLoop.AcceptErrors);
        writer.WriteNumber(keys.BytesEchoed, Interlocked.Read(ref _bytesEchoed));
        writer.WriteNumber(keys.ProtocolErrors, Interlocked.Read(ref _protocolErrors));
        writer.WriteNumber(keys.TruncatedFrames, Interlocked.Read(ref _truncatedFrames));
        writer.WriteStartObject(keys.Verdicts);
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
                writer.WriteString(ArmKeys.Common.Record.Type, "tcpSummary");
                WriteTotals(writer, TcpTotalsKeys.Summary);
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
            if (command.Outcome is not { } outcome)
            {
                // Teardown closed the socket under the connection before the protocol measured
                // anything: no verdict and no connection record, because teardown is not a data
                // point. The accept itself is already counted by the connection census above.
                return;
            }

            var endedTicks = Stopwatch.GetTimestamp();
            Interlocked.Add(ref _bytesEchoed, outcome.BytesEchoed);
            Interlocked.Add(ref _protocolErrors, outcome.ProtocolErrors);
            if (outcome.Truncated)
            {
                Interlocked.Increment(ref _truncatedFrames);
            }

            Interlocked.Increment(ref _verdicts[(int)outcome.Verdict]);

            // The ledger's policy swallows and counts an I/O failure on its own; this guard is what
            // covers a body the sink was asked to propagate, and it keeps the fire-and-forget task
            // from faulting into the shutdown join. The connection is reset so an unrecorded
            // attempt is visible to its peer rather than indistinguishable from a recorded one, and
            // the listener keeps serving.
            try
            {
                await WriteConnectionAsync(reader.Header.ConnectionId, command, outcome, peer, startedTicks, endedTicks).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await TargetLog.ReportAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"e2e target: connection {reader.Header.ConnectionId} could not be recorded ({_ledger.WriteErrors} ledger write error(s) so far): {exception.GetType().Name}: {exception.Message}")).ConfigureAwait(false);
                await TargetRunner.WriteErrorRecordAsync(_ledger, exception).ConfigureAwait(false);
                socket.LingerState = new LingerOption(enable: true, 0);
            }
        }
    }

    private async ValueTask WriteConnectionAsync(uint connectionId, CommandOutcome command, TcpModeOutcome outcome, string peer, long startedTicks, long endedTicks)
    {
        var modeText = command.ModeKnown ? TcpCommand.Name(command.Mode) : "unknown";
        var verdictText = TcpCommand.Name(outcome.Verdict);
        var bytesEchoed = outcome.BytesEchoed;
        await _ledger.WriteAsync(
            writer =>
            {
                writer.WriteString(ArmKeys.Common.Record.Type, "tcp");
                writer.WriteNumber(ArmKeys.Ledger.TcpRecord.ConnectionId, connectionId);
                writer.WriteString(ArmKeys.Ledger.TcpRecord.Mode, modeText);
                writer.WriteNumber(ArmKeys.Ledger.TcpRecord.ExpectedBytes, command.ExpectedBytes);
                writer.WriteNumber(ArmKeys.Ledger.TcpRecord.BytesEchoed, bytesEchoed);
                writer.WriteString(ArmKeys.Ledger.TcpRecord.Verdict, verdictText);
                writer.WriteString(ArmKeys.Ledger.TcpRecord.Peer, peer);
                writer.WriteNumber(ArmKeys.Ledger.TcpRecord.StartedTicks, startedTicks);
                writer.WriteNumber(ArmKeys.Ledger.TcpRecord.EndedTicks, endedTicks);
            },
            CancellationToken.None).ConfigureAwait(false);
    }
}
