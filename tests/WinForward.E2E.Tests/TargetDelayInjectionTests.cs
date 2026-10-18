using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WinForward.E2E.Cli;
using WinForward.E2E.Client;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Target;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The positive control for the latency instrument: a deterministic delay in the target's measured
/// response path must move the observed p99.9 by that delay, and the same run without the injection
/// must leave the tail where it was. The sidecar beside the ledger publishes the effective delay,
/// so a calibration run can be audited from its own artifacts instead of trusting the command line
/// that started it.
/// </summary>
public sealed class TargetDelayInjectionTests
{
    private const int InjectionMilliseconds = 1000;

    // 50 requests/s for 3 s puts 150 samples in each histogram, so p99.9 is the last sample and a
    // delay applied to every response is visible without relying on a percentile interpolator.
    private const int Seconds = 3;

    private const int RatePerSecond = 50;

    private const long InjectedTailFloorMicroseconds = 900_000;

    private const long InjectedTailCeilingMicroseconds = 5_000_000;

    private const long ControlTailCeilingMicroseconds = 500_000;

    [Fact]
    public async Task AThousandMillisecondInjectionMovesTheObservedLatencyTailToAboutOneSecond()
    {
        var (tcp, udp) = await RunLatencyAsync(InjectionMilliseconds).ConfigureAwait(false);

        Assert.True(Count(tcp) > 0, "the tcp lane recorded no latency sample");
        Assert.True(Count(udp) > 0, "the udp lane recorded no latency sample");
        Assert.True(
            TailMicroseconds(tcp) is >= InjectedTailFloorMicroseconds and <= InjectedTailCeilingMicroseconds,
            string.Create(CultureInfo.InvariantCulture, $"the tcp p99.9 with a {InjectionMilliseconds} ms injection is {TailMicroseconds(tcp) / 1000.0:F1} ms, not about {InjectionMilliseconds} ms"));
        Assert.True(
            TailMicroseconds(udp) is >= InjectedTailFloorMicroseconds and <= InjectedTailCeilingMicroseconds,
            string.Create(CultureInfo.InvariantCulture, $"the udp p99.9 with a {InjectionMilliseconds} ms injection is {TailMicroseconds(udp) / 1000.0:F1} ms, not about {InjectionMilliseconds} ms"));
    }

    [Fact]
    public async Task NoInjectionLeavesTheObservedLatencyTailWhereItWas()
    {
        var (tcp, udp) = await RunLatencyAsync(injectDelayMs: 0).ConfigureAwait(false);

        Assert.True(Count(tcp) > 0, "the tcp lane recorded no latency sample");
        Assert.True(Count(udp) > 0, "the udp lane recorded no latency sample");
        Assert.True(
            TailMicroseconds(tcp) < ControlTailCeilingMicroseconds,
            string.Create(CultureInfo.InvariantCulture, $"the tcp p99.9 without an injection is {TailMicroseconds(tcp) / 1000.0:F1} ms, so the zero-injection control moved"));
        Assert.True(
            TailMicroseconds(udp) < ControlTailCeilingMicroseconds,
            string.Create(CultureInfo.InvariantCulture, $"the udp p99.9 without an injection is {TailMicroseconds(udp) / 1000.0:F1} ms, so the zero-injection control moved"));
    }

    [Fact]
    public async Task TheDnsResponderDelaysItsResponses()
    {
        var dnsPort = ArmRunFixture.FreeDualPort();
        using var stream = new MemoryStream();
        await using var ledger = new JsonlSink(
            stream,
            JsonlPolicy.SwallowAndCount,
            TargetRunner.WriteLedgerEnvelope("injection"),
            TimeSpan.FromMinutes(1));
        await using var dns = new DnsServer(new IPEndPoint(IPAddress.Loopback, dnsPort), ledger, workerCount: 1, injectDelayMs: 200);

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = dns.RunAsync(shutdown.Token);
        try
        {
            var query = new byte[512];
            var queryLength = DnsQueryBuilder.BuildQuery(query, transactionId: 7, index: 1, cnameEvery: 0);

            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            await udp.ConnectAsync(new IPEndPoint(IPAddress.Loopback, dnsPort), shutdown.Token).ConfigureAwait(false);
            var udpStarted = Stopwatch.GetTimestamp();
            await udp.SendAsync(query.AsMemory(0, queryLength), SocketFlags.None, shutdown.Token).ConfigureAwait(false);
            var udpResponse = new byte[4096];
            Assert.True(await udp.ReceiveAsync(udpResponse, SocketFlags.None, shutdown.Token).ConfigureAwait(false) > 0);
            var udpElapsed = Stopwatch.GetElapsedTime(udpStarted);

            using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await tcp.ConnectAsync(new IPEndPoint(IPAddress.Loopback, dnsPort), shutdown.Token).ConfigureAwait(false);
            var tcpStarted = Stopwatch.GetTimestamp();
            var prefix = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)queryLength);
            await tcp.SendAsync(prefix, SocketFlags.None, shutdown.Token).ConfigureAwait(false);
            await tcp.SendAsync(query.AsMemory(0, queryLength), SocketFlags.None, shutdown.Token).ConfigureAwait(false);
            var lengthBuffer = new byte[2];
            Assert.Equal(ReadExactOutcome.Complete, await SocketIO.ReadExactAsync(tcp, lengthBuffer, shutdown.Token).ConfigureAwait(false));
            var responseLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);
            Assert.Equal(ReadExactOutcome.Complete, await SocketIO.ReadExactAsync(tcp, new byte[responseLength], shutdown.Token).ConfigureAwait(false));
            var tcpElapsed = Stopwatch.GetElapsedTime(tcpStarted);

            Assert.True(udpElapsed >= TimeSpan.FromMilliseconds(150), string.Create(CultureInfo.InvariantCulture, $"the udp dns response arrived after {udpElapsed.TotalMilliseconds:F1} ms, not after the injected 200 ms"));
            Assert.True(tcpElapsed >= TimeSpan.FromMilliseconds(150), string.Create(CultureInfo.InvariantCulture, $"the tcp dns response arrived after {tcpElapsed.TotalMilliseconds:F1} ms, not after the injected 200 ms"));
        }
        finally
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
            await running.ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task TheInjectionSidecarPublishesTheEffectiveDelay()
    {
        var tcpPort = ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp);
        var udpPort = ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp);
        var dnsPort = ArmRunFixture.FreeDualPort();
        var ledgerPath = Path.Combine(Path.GetTempPath(), $"wf-injection-{Guid.NewGuid():N}.jsonl");

        string[] arguments =
        [
            "--bind", "127.0.0.1",
            "--tcp-port", tcpPort.ToString(CultureInfo.InvariantCulture),
            "--udp-port", udpPort.ToString(CultureInfo.InvariantCulture),
            "--dns-port", dnsPort.ToString(CultureInfo.InvariantCulture),
            "--inject-delay-ms", "250",
            "--label", "injection",
            "--ledger", ledgerPath,
        ];

        Assert.True(TargetOptions.TryCreate(arguments, out var options, out var error), error);

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = TargetRunner.RunAsync(options, shutdown.Token);
        try
        {
            await ArmRunFixture.EchoOneDatagramAsync(udpPort, shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
            Assert.Equal(ExitCodes.Success, await running.ConfigureAwait(false));
        }

        var sidecarPath = TargetInjectionFile.PathFor(ledgerPath);
        Assert.True(File.Exists(sidecarPath), $"the target wrote no sidecar at {sidecarPath}");

        using (var sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(sidecarPath, CancellationToken.None).ConfigureAwait(false)))
        {
            Assert.Equal(250, sidecar.RootElement.GetProperty("injectDelayMs").GetInt32());
            Assert.Equal("injection", sidecar.RootElement.GetProperty("label").GetString());
        }

        var ledger = await File.ReadAllTextAsync(ledgerPath, CancellationToken.None).ConfigureAwait(false);
        Assert.DoesNotContain("injectDelayMs", ledger);

        File.Delete(ledgerPath);
        File.Delete(sidecarPath);
    }

    private static async Task<(JsonElement Tcp, JsonElement Udp)> RunLatencyAsync(int injectDelayMs)
    {
        var tcpPort = ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp);
        var udpPort = ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp);
        var dnsPort = ArmRunFixture.FreeDualPort();

        string[] arguments =
        [
            "--bind", "127.0.0.1",
            "--tcp-port", tcpPort.ToString(CultureInfo.InvariantCulture),
            "--udp-port", udpPort.ToString(CultureInfo.InvariantCulture),
            "--dns-port", dnsPort.ToString(CultureInfo.InvariantCulture),
            "--inject-delay-ms", injectDelayMs.ToString(CultureInfo.InvariantCulture),
        ];
        Assert.True(TargetOptions.TryCreate(arguments, out var options, out var error), error);

        using var stream = new MemoryStream();
        await using var ledger = new JsonlSink(
            stream,
            JsonlPolicy.SwallowAndCount,
            TargetRunner.WriteLedgerEnvelope("injection"),
            TimeSpan.FromMinutes(1));
        await using var tcp = new TcpTargetServer(new IPEndPoint(IPAddress.Loopback, tcpPort), ledger, options.InjectDelayMs);
        await using var udp = new UdpEchoServer(new IPEndPoint(IPAddress.Loopback, udpPort), ledger, receiverCount: 2, options.InjectDelayMs);

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var tcpRunning = tcp.RunAsync(shutdown.Token);
        var udpRunning = udp.RunAsync(shutdown.Token);
        await WaitForUdpReceiverAsync(udp, shutdown.Token).ConfigureAwait(false);

        await using var fixture = ArmRunFixture.Create(tcpPort, udpPort, dnsPort);
        var context = fixture.ContextFor(new ArmSpec
        {
            Name = "LAT",
            Kind = "latency",
            Seconds = Seconds,
            RatePerSecond = RatePerSecond,
            Protocol = "tcp+udp",
            PayloadBytes = 120,
            Window = 4096,
            Lanes = 1,
        });

        await ArmDispatch.RunAsync(context).ConfigureAwait(false);

        await shutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(tcpRunning, udpRunning).ConfigureAwait(false);

        return (Histogram(context.Latency.TcpRtt), Histogram(context.Latency.UdpRtt));
    }

    private static async Task WaitForUdpReceiverAsync(UdpEchoServer server, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (server.StartedReceivers == 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new TimeoutException("the target's receive loop did not start within 5 s");
            }

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private static long Count(JsonElement histogram) => histogram.GetProperty(ArmKeys.Common.LatencyRecord.Histogram.Count).GetInt64();

    private static long TailMicroseconds(JsonElement histogram) => (long)histogram.GetProperty(ArmKeys.Common.LatencyRecord.Histogram.P999Us).GetDouble();

    private static JsonElement Histogram(LogHistogram histogram)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            histogram.Snapshot().WriteTo(writer, "rtt");
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.GetProperty("rtt").Clone();
    }
}
