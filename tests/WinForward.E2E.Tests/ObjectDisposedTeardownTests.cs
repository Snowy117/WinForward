using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Target;
using WinForward.E2E.Wire;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The behavioural half of the teardown contract (D19.2 ⑨): a site whose socket teardown closed books
/// nothing, while the same site's arm for a failure the harness really saw still books exactly what it
/// always did. Each fact drives one of the two shapes and reads the value back out of the published
/// record, so a counter that stopped moving for the wrong reason is as red as one that moved for
/// teardown.
/// </summary>
/// <remarks>
/// The teardown half is driven where the socket is reachable: the protocol takes its reader as an
/// argument, so a socket disposed before the first read reaches its catch directly. The other four sites
/// own their socket inside the call that wraps the catch, so nothing in the harness can dispose one under
/// them (measured: a disposal under a pending receive ends the operation as
/// <c>SocketException(OperationAborted)</c>, which their socket-error arm books -- not the teardown arm);
/// their teardown shape is asserted by <see cref="ObjectDisposedCatchGateTests"/> instead, and the facts
/// here pin the arm that does move.
/// </remarks>
public sealed class ObjectDisposedTeardownTests
{
    /// <summary>Enough room for the query the DNS helper builds and the answer it reads back.</summary>
    private const int MaxDnsMessageLength = 512;

    [Fact]
    public async Task AConnectionTornDownUnderTheProtocolPublishesNoVerdict()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Dispose();

        var command = await TcpConnectionProtocol.RunConnectionAsync(socket, new FrameStreamReader(socket), CancellationToken.None);

        Assert.Null(command.Outcome);
        Assert.False(command.ModeKnown);
    }

    [Fact]
    public async Task AConnectionTheArmCancelledStillPublishesItsErrorVerdict()
    {
        var (server, client) = await ConnectedPairAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var command = await TcpConnectionProtocol.RunConnectionAsync(server, new FrameStreamReader(server), cancellation.Token);

        // The reader stopped before any frame arrived and the cancellation is what ended it: that is the
        // verdict this arm has always published for a connection it could not measure for a reason the
        // run caused, and the teardown arm must not take it away.
        Assert.NotNull(command.Outcome);
        Assert.Equal(TcpVerdict.Error, command.Outcome.Value.Verdict);
        Assert.Equal(0, command.Outcome.Value.ProtocolErrors);
        Assert.False(command.Outcome.Value.Truncated);

        server.Dispose();
        client.Dispose();
    }

    [Fact]
    public async Task ABulkLoopStillBooksAConnectionItCouldNotOpen()
    {
        var counters = new MixCounters(desktops: 1);
        await using var fixture = ArmRunFixture.Create(
            DeadTcpPort(),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp));
        var startTicks = Clock.Now;

        await MixBulkLoop.BulkLoopAsync(
            fixture.ContextFor(new ArmSpec { Name = "MIX", Kind = "mix" }),
            counters,
            desktopIndex: 0,
            startTicks,
            startTicks + Clock.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(1, Interlocked.Read(ref counters._bulkErrors));
    }

    [Fact]
    public async Task APageLoopStillBooksEveryConnectionItCouldNotOpen()
    {
        var counters = new MixCounters(desktops: 1);
        await using var fixture = ArmRunFixture.Create(
            DeadTcpPort(),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp),
            ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp));
        var startTicks = Clock.Now;
        using var cancellation = new CancellationTokenSource();
        var pages = MixPageLoop.PageLoopAsync(
            fixture.ContextFor(new ArmSpec { Name = "MIX", Kind = "mix" }),
            counters,
            desktopIndex: 0,
            startTicks,
            startTicks + Clock.FromSeconds(30),
            cancellation.Token);

        try
        {
            await WaitUntilAsync(() => Interlocked.Read(ref counters._pageErrors) >= MixPageLoop.PageConnections, TimeSpan.FromSeconds(15));
            Assert.Equal(MixPageLoop.PageConnections, Interlocked.Read(ref counters._pageErrors));
            Assert.Equal(0, Interlocked.Read(ref counters.PageConnectionsPerDesktop[0]));
        }
        finally
        {
            await cancellation.CancelAsync();
            await pages;
        }
    }

    [Fact]
    public async Task TheDnsListenerStillCountsAStreamItsPeerAborted()
    {
        using var stream = new MemoryStream();
        await using var ledger = new JsonlSink(stream, JsonlPolicy.SwallowAndCount, TargetRunner.WriteLedgerEnvelope("teardown"), TimeSpan.FromMinutes(1));
        var dnsPort = ArmRunFixture.FreeDualPort();
        await using var dns = new DnsServer(new IPEndPoint(IPAddress.Loopback, dnsPort), ledger, workerCount: 2);

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = dns.RunAsync(shutdown.Token);

        // The query is answered before the cancellation, and the connection stays open until after it, so
        // the handler is back in its length-prefix read when the run ends: the cancellation is what ends
        // that read, which is the arm this counter has always had.
        using var client = await AskOneTcpQueryAsync(dnsPort, shutdown.Token);

        await shutdown.CancelAsync();
        await serving;

        Assert.Equal(1, Totals(dns, ArmKeys.Ledger.DnsSummary.TcpAborted));
    }

    /// <summary>
    /// The number one <c>dnsSummary</c> path carries, read back from the writer the ledger uses rather
    /// than from the field behind it.
    /// </summary>
    private static double Totals(DnsServer dns, string path)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            // The totals block is a body, not a document: the sink opens the record the writer fills.
            writer.WriteStartObject();
            dns.WriteTotals(writer, DnsTotalsKeys.Summary);
            writer.WriteEndObject();
        }

        return JsonPaths.FlattenJsonl(Encoding.UTF8.GetString(stream.ToArray()))[path][0].Value.GetDouble();
    }

    /// <summary>
    /// One DNS query over TCP, answered by the listener: the two-byte length prefix and the query are what
    /// the arm's own TCP phase writes. The connection is left open and handed to the caller, which is what
    /// keeps the listener's handler in its read.
    /// </summary>
    private static async Task<Socket> AskOneTcpQueryAsync(int port, CancellationToken cancellationToken)
    {
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);

            var message = new byte[MaxDnsMessageLength];
            var length = DnsQueryBuilder.BuildQuery(message, transactionId: 1, index: 0, cnameEvery: 0);
            Assert.True(length > 0, "the query builder produced no query");

            var framed = new byte[sizeof(ushort) + length];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)length);
            message.AsSpan(0, length).CopyTo(framed.AsSpan(sizeof(ushort)));
            await client.SendAsync(framed, SocketFlags.None, cancellationToken).ConfigureAwait(false);

            var prefix = new byte[sizeof(ushort)];
            var read = await client.ReceiveAsync(prefix, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            Assert.Equal(prefix.Length, read);

            var answerLength = BinaryPrimitives.ReadUInt16BigEndian(prefix);
            var answer = new byte[answerLength];
            var answered = 0;
            while (answered < answer.Length)
            {
                var part = await client.ReceiveAsync(answer.AsMemory(answered), SocketFlags.None, cancellationToken).ConfigureAwait(false);
                Assert.NotEqual(0, part);
                answered += part;
            }

            Assert.True(
                DnsWire.TryParseResponse(answer, out var responseId, out _, out _) && responseId == 1,
                "the answer is not the response to the query");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>A port nothing holds: the connector is refused rather than left waiting.</summary>
    private static int DeadTcpPort() => ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp);

    /// <summary>Two ends of one loopback connection, both owned by the caller.</summary>
    private static async Task<(Socket Server, Socket Client)> ConnectedPairAsync()
    {
        using var listener = Sockets.BindTcpListener(new IPEndPoint(IPAddress.Loopback, 0));
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var accepting = listener.AcceptAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!, CancellationToken.None).ConfigureAwait(false);
        return (await accepting.ConfigureAwait(false), client);
    }

    /// <summary>
    /// Waits for a counter another thread books, with a bound: an arm's own thread writes it, so the fact
    /// synchronizes on the counter rather than on a proxy for the arm's progress.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan bound)
    {
        var deadline = Environment.TickCount64 + (long)bound.TotalMilliseconds;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Yield();
        }
    }
}
