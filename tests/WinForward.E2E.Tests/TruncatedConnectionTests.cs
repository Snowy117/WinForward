using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinForward.E2E.Client;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Target;
using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// What the target does with a peer that stops writing inside a frame (D19.3 D): the connection is a
/// protocol error and earns no trailer, while the half-close a mode actually asks for still does. The
/// pair is the point -- a target that had simply stopped writing trailers would pass either fact alone.
/// </summary>
/// <remarks>
/// Both facts send the same <c>halfClose</c> command first, so the only difference between them is
/// whether the stream ends at a frame boundary or ten bytes into the next frame. That is what makes
/// "the mode was read and the trailer was still skipped" observable from the wire.
/// </remarks>
public sealed class TruncatedConnectionTests
{
    private const string Label = "truncated";

    private const uint ConnectionId = 0x5452_0001u;

    private const int PayloadBytes = 32;

    // Long enough that the periodic flush never fires inside a fact; a memory stream needs no draining
    // for its bytes to be readable right after the write.
    private static readonly TimeSpan s_noFlush = TimeSpan.FromMinutes(1);

    private const int TrailerFrameLength = FrameCodec.HeaderSize + TrailerProtocol.PayloadBytes + FrameCodec.TrailerSize;

    [Fact]
    public async Task AConnectionCutInsideAFrameIsAProtocolErrorAndEarnsNoTrailer()
    {
        var port = ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp);
        var run = await RunTargetAsync(port, async cancellationToken =>
        {
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);
            await SendHalfCloseCommandAsync(client, cancellationToken).ConfigureAwait(false);

            // Ten bytes of the frame that would have followed: the mode is committed to a trailer by
            // now, so anything the target writes after this point is the trailer this fact denies.
            var frame = new FrameBuffer(PayloadBytes);
            frame.Build(ConnectionId, sequence: 1, sendTicks: 0);
            await client.SendAsync(frame.Memory[..10], SocketFlags.None, cancellationToken).ConfigureAwait(false);
            SocketOps.ShutdownQuietly(client, SocketShutdown.Send);

            return await ReadToEndAsync(client, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        Assert.Equal(0, run.Received);
        Assert.Equal("protocolError", Verdict(run.Ledger));
        Assert.Equal(1, Number(run.Ledger, "tcpSummary", ArmKeys.Ledger.TcpSummary.TruncatedFrames));
        Assert.Equal(1, Number(run.Ledger, "tcpSummary", ArmKeys.Ledger.TcpSummary.ProtocolErrors));
    }

    [Fact]
    public async Task ACleanHalfCloseStillReceivesTheWholeTrailer()
    {
        var port = ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp);
        var run = await RunTargetAsync(port, async cancellationToken =>
        {
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);
            await SendHalfCloseCommandAsync(client, cancellationToken).ConfigureAwait(false);
            SocketOps.ShutdownQuietly(client, SocketShutdown.Send);

            return await ReadToEndAsync(client, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        Assert.Equal(TrailerFrameLength * TrailerProtocol.FrameCount, run.Received);
        Assert.Equal(TrailerProtocol.TotalBytes, run.TrailerPayload);
        Assert.Equal(TrailerProtocol.FrameCount, run.TrailerFrames);
        Assert.Equal("halfClose", Verdict(run.Ledger));
        Assert.Equal(0, Number(run.Ledger, "tcpSummary", ArmKeys.Ledger.TcpSummary.TruncatedFrames));
    }

    /// <summary>
    /// The DNS listener reads a length prefix rather than a framed stream, so its truncation is its own
    /// (D19.3 C): the responder must not name the frame reader's status, and the two keys that share the
    /// spelling <c>truncatedFrames</c> stay two constants.
    /// </summary>
    [Fact]
    public void TheDnsListenerCountsItsOwnShortReadsRatherThanTheFrameReadersTruncation()
    {
        var source = File.ReadAllText(Path.Combine(RepoPaths.Root, "benchmarks", "WinForward.E2E", "Target", "DnsServer.cs"));

        Assert.DoesNotContain(nameof(FrameReadStatus), source, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(FrameStreamReader), source, StringComparison.Ordinal);
        Assert.Contains(nameof(SocketIO.ReadExactAsync), source, StringComparison.Ordinal);
        Assert.Equal(ArmKeys.Ledger.TcpSummary.TruncatedFrames, ArmKeys.Ledger.DnsSummary.TruncatedFrames);
        Assert.NotEqual(
            typeof(ArmKeys.Ledger.TcpSummary).GetField(nameof(ArmKeys.Ledger.TcpSummary.TruncatedFrames)),
            typeof(ArmKeys.Ledger.DnsSummary).GetField(nameof(ArmKeys.Ledger.DnsSummary.TruncatedFrames)));
    }

    /// <summary>
    /// One command frame that asks for a half-close, which is the mode whose contract is a trailer.
    /// </summary>
    private static ValueTask SendHalfCloseCommandAsync(Socket client, CancellationToken cancellationToken) =>
        SocketOps.SendCommandAsync(client, ConnectionId, TcpMode.HalfClose, expectedBytes: 0, cancellationToken);

    /// <summary>
    /// Reads until the peer closes, counting the bytes and decoding the frames they carry: a cut stream
    /// and a clean one both end at end of stream, and only the bytes tell them apart. Each receive is
    /// bounded by the scratch buffer, so a frame split across two of them is still decoded whole.
    /// </summary>
    private static async Task<WireBytes> ReadToEndAsync(Socket client, CancellationToken cancellationToken)
    {
        var kept = new byte[(TrailerFrameLength * TrailerProtocol.FrameCount) + PayloadBytes];
        var scratch = new byte[PayloadBytes];
        var filled = 0;
        var received = 0;
        while (true)
        {
            var read = await client.ReceiveAsync(scratch, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            received += read;
            var take = Math.Min(read, kept.Length - filled);
            scratch.AsSpan(0, take).CopyTo(kept.AsSpan(filled));
            filled += take;
        }

        var trailerBytes = 0;
        var trailerFrames = 0;
        var offset = 0;
        while (offset < filled
            && FrameCodec.TryDecode(kept.AsSpan(offset, filled - offset), out _, out var payload, out _))
        {
            trailerBytes += payload.Length;
            trailerFrames++;
            offset += FrameCodec.HeaderSize + payload.Length + FrameCodec.TrailerSize;
        }

        return new WireBytes(received, trailerBytes, trailerFrames);
    }

    /// <summary>
    /// Runs one TCP target over an in-memory ledger: the caller drives the port, the listener is joined
    /// before the summary is written, and the ledger comes back as text for the two records these facts
    /// read.
    /// </summary>
    private static async Task<TargetRun> RunTargetAsync(int port, Func<CancellationToken, Task<WireBytes>> drive)
    {
        using var stream = new MemoryStream();
        await using var ledger = new JsonlSink(stream, JsonlPolicy.SwallowAndCount, TargetRunner.WriteLedgerEnvelope(Label), s_noFlush);
        await using var target = new TcpTargetServer(new IPEndPoint(IPAddress.Loopback, port), ledger);

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = target.RunAsync(shutdown.Token);
        WireBytes wire;
        try
        {
            wire = await drive(shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
            await serving.ConfigureAwait(false);
        }

        await target.WriteSummaryAsync(CancellationToken.None).ConfigureAwait(false);
        await ledger.CompleteAsync().ConfigureAwait(false);
        return new TargetRun(wire, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static string Verdict(string ledger) =>
        JsonPaths.FlattenJsonl(LineOf(ledger, "tcp"))[ArmKeys.Ledger.TcpRecord.Verdict][0].Value.GetString()!;

    private static double Number(string ledger, string kind, string path) =>
        JsonPaths.FlattenJsonl(LineOf(ledger, kind))[path][0].Value.GetDouble();

    /// <summary>
    /// The one ledger line of a kind: a fact's run opens one connection and writes one summary, so a
    /// second line of the same kind would mean the fixture drove more than the fact says it did.
    /// </summary>
    private static string LineOf(string ledger, string kind)
    {
        var found = new List<string>();
        foreach (var line in ledger.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty(ArmKeys.Common.Record.Type).GetString() == kind)
            {
                found.Add(line);
            }
        }

        return Assert.Single(found);
    }

    /// <summary>What one connection's stream carried, in bytes and in decoded trailer frames.</summary>
    private readonly record struct WireBytes(int Received, int TrailerPayload, int TrailerFrames);

    /// <summary>One run's wire outcome and the ledger it published.</summary>
    private readonly record struct TargetRun(WireBytes Wire, string Ledger)
    {
        internal int Received => Wire.Received;

        internal int TrailerPayload => Wire.TrailerPayload;

        internal int TrailerFrames => Wire.TrailerFrames;
    }
}
