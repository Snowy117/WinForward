using System.Net;
using System.Net.Sockets;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Target;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The other half of the ledger's shape contract: what the counters behind the published keys do when
/// the events they count actually happen. The families, the declared paths and the composition that
/// publishes them live in <c>LedgerShapeTests.cs</c>; these facts drive one failure or one refused
/// accept each and read the number back out of the ledger.
/// </summary>
public sealed partial class LedgerShapeTests
{
    /// <summary>
    /// The ledger's <c>error</c> record carries the innermost exception rather than the wrapper it
    /// arrived in: the family already says the record is an error, so the leaf a reader needs is the
    /// cause, and the derivation is the client's own (<c>GetBaseException</c>) one level down.
    /// </summary>
    [Fact]
    public async Task TheErrorRecordNamesTheInnermostCauseOfTheFailure()
    {
        var ledger = await PublishLedgerAsync();

        var error = JsonPaths.FlattenJsonl(First(ledger, ErrorKind));
        Assert.Equal(nameof(SocketException), error[ArmKeys.Ledger.ErrorRecord.Detail][0].Value.GetString());

        // The wrapper's own type is nowhere in the record: publishing it beside the cause is what the
        // client's arm files do and what this family exists to replace.
        Assert.DoesNotContain(
            nameof(InvalidOperationException),
            error.Values.Select(observations => observations[0].Value.ToString()));
    }

    /// <summary>
    /// A refused accept is counted where the loop swallows it and reaches the ledger in its own
    /// listener's family, at the summary's root and one level down. The TCP listener and the second DNS
    /// listener are handed listeners that refuse; the first DNS listener runs a real stream listener and
    /// stays at zero, so the two counts are two listeners rather than one written twice.
    /// </summary>
    [Fact]
    public async Task ARefusedAcceptIsCountedInItsOwnListenersFamilyAtBothLevels()
    {
        using var stream = new MemoryStream();
        await using var ledger = new JsonlSink(stream, JsonlPolicy.SwallowAndCount, TargetRunner.WriteLedgerEnvelope(Label), s_noFlush);

        var udpPort = ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp);
        var dnsPort = ArmRunFixture.FreeDualPort();
        var dnsAltPort = ArmRunFixture.FreeDualPort();

        var (tcpLoop, _) = RefusingLoop(refusals: 2);
        var (dnsAltLoop, _) = RefusingLoop(refusals: 2);

        await using var udp = new UdpEchoServer(new IPEndPoint(IPAddress.Loopback, udpPort), ledger, Workers);
        await using var tcp = new TcpTargetServer(tcpLoop, ledger);
        await using var dns = new DnsServer(new IPEndPoint(IPAddress.Loopback, dnsPort), ledger, Workers);
        await using var dnsAlt = new DnsServer(
            Sockets.BindUdp(new IPEndPoint(IPAddress.Loopback, dnsAltPort)),
            dnsAltLoop,
            ledger,
            Workers,
            dnsAltPort);

        using var shutdown = new CancellationTokenSource();
        var accepting = tcp.RunAsync(shutdown.Token);
        var serving = dns.RunAsync(shutdown.Token);
        var servingAlt = dnsAlt.RunAsync(shutdown.Token);

        // A refusal is booked on the loop's own thread, so the fact waits for the two counters rather
        // than assuming that a scheduling round has passed.
        await WaitForAcceptErrorsAsync(tcpLoop, dnsAltLoop);

        await shutdown.CancelAsync();
        await Task.WhenAll(accepting, serving, servingAlt);

        await TargetRunner.WriteSummariesAsync(ledger, tcp, udp, dns, dnsAlt, startedTicks: 1, endedTicks: 2);
        await ledger.CompleteAsync();

        var records = Parse(stream);
        var tcpSummary = JsonPaths.FlattenJsonl(Last(records, TcpSummaryKind));
        var target = JsonPaths.FlattenJsonl(Last(records, TargetSummaryKind));
        var realListener = JsonPaths.FlattenJsonl(records[DnsSummaryKind][0]);
        var refusedListener = JsonPaths.FlattenJsonl(records[DnsSummaryKind][1]);

        Assert.True(Number(tcpSummary, ArmKeys.Ledger.TcpSummary.AcceptErrors) >= 1, "the tcp listener refused no accept");
        Assert.Equal(
            Number(tcpSummary, ArmKeys.Ledger.TcpSummary.AcceptErrors),
            Number(target, $"{ArmKeys.Ledger.TargetSummary.Tcp}/{ArmKeys.Ledger.TargetSummary.TcpTotals.AcceptErrors}"));

        Assert.True(Number(refusedListener, ArmKeys.Ledger.DnsSummary.AcceptErrors) >= 1, "the second dns listener refused no accept");
        Assert.Equal(0, Number(realListener, ArmKeys.Ledger.DnsSummary.AcceptErrors));
        Assert.Equal(
            Number(refusedListener, ArmKeys.Ledger.DnsSummary.AcceptErrors),
            Number(target, $"{ArmKeys.Ledger.TargetSummary.DnsAlt}/{ArmKeys.Ledger.TargetSummary.DnsTotals.AcceptErrors}"));
        Assert.Equal(
            Number(realListener, ArmKeys.Ledger.DnsSummary.AcceptErrors),
            Number(target, $"{ArmKeys.Ledger.TargetSummary.Dns}/{ArmKeys.Ledger.TargetSummary.DnsTotals.AcceptErrors}"));
    }

    /// <summary>
    /// A refusal is counted and the listener keeps serving: the connection that arrives after the
    /// refused accepts is the loop's own, so the count makes a refusing listener visible without the
    /// refusal changing what the listener does.
    /// </summary>
    [Fact]
    public async Task ARefusedAcceptIsCountedAndTheListenerKeepsServing()
    {
        var (loop, endPoint) = RefusingLoop(refusals: 2);
        using (loop)
        {
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var shutdown = new CancellationTokenSource();
            var accepting = loop.RunAsync(
                socket =>
                {
                    socket.Dispose();
                    handled.TrySetResult();
                    return Task.CompletedTask;
                },
                shutdown.Token);

            var deadline = Environment.TickCount64 + 5000;
            while (loop.AcceptErrors < 2 && Environment.TickCount64 < deadline)
            {
                await Task.Yield();
            }

            Assert.Equal(2, loop.AcceptErrors);

            using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                await client.ConnectAsync(endPoint, shutdown.Token).ConfigureAwait(false);
                await handled.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }

            await shutdown.CancelAsync();
            await accepting;

            // The accept that succeeded is not a refusal: the count moved for the two scripted failures
            // and for nothing else.
            Assert.Equal(2, loop.AcceptErrors);
        }
    }

    /// <summary>
    /// The published receiver count is the loops that started rather than the number the server was
    /// built with: it reads zero before any loop has run and the loop count once they all have, so a
    /// listener that never started one cannot publish a healthy number.
    /// </summary>
    [Fact]
    public async Task ThePublishedUdpReceiverCountIsTheLoopsThatStarted()
    {
        using var stream = new MemoryStream();
        await using var ledger = new JsonlSink(stream, JsonlPolicy.SwallowAndCount, TargetRunner.WriteLedgerEnvelope(Label), s_noFlush);

        var tcpPort = ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp);
        var udpPort = ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp);
        var dnsPort = ArmRunFixture.FreeDualPort();
        var dnsAltPort = ArmRunFixture.FreeDualPort();

        await using var tcp = new TcpTargetServer(new IPEndPoint(IPAddress.Loopback, tcpPort), ledger);
        await using var udp = new UdpEchoServer(new IPEndPoint(IPAddress.Loopback, udpPort), ledger, Receivers);
        await using var dns = new DnsServer(new IPEndPoint(IPAddress.Loopback, dnsPort), ledger, Workers);
        await using var dnsAlt = new DnsServer(new IPEndPoint(IPAddress.Loopback, dnsAltPort), ledger, Workers);

        Assert.Equal(0L, udp.StartedReceivers);

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var receiving = udp.RunAsync(shutdown.Token);
        await ArmRunFixture.EchoOneDatagramAsync(udpPort, shutdown.Token);
        await shutdown.CancelAsync();
        await receiving;

        // Every loop books its own start, so the sum is the number of loops that ran rather than a copy
        // of the argument: the count is above the default formula's ceiling, which no default can reach.
        Assert.Equal(Receivers, (int)udp.StartedReceivers);

        await TargetRunner.WriteSummariesAsync(ledger, tcp, udp, dns, dnsAlt, startedTicks: 1, endedTicks: 2);
        await ledger.CompleteAsync();

        var target = JsonPaths.FlattenJsonl(Last(Parse(stream), TargetSummaryKind));
        Assert.Equal(
            Receivers,
            Number(target, $"{ArmKeys.Ledger.TargetSummary.Udp}/{ArmKeys.Ledger.TargetSummary.UdpTotals.UdpReceivers}"));
    }
}
