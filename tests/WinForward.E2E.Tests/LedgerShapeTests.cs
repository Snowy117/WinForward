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
/// The shape contract of the target's ledger: the bytes the four record families publish, flattened with
/// the shared path alphabet, against the paths <c>ArmKeys.Ledger</c> declares. Both sides of the
/// comparison are the production ones -- the sink, its envelope, the servers' record writers and the
/// runner's summary composition -- so a key written to the wrong level, written twice, declared but
/// never written, or written but never declared shows up as a two-way diff instead of an analyzer
/// quietly reading <c>n/a</c>.
/// </summary>
/// <remarks>
/// The ledger publishes two states of every counter: the run before any traffic (zeros, an empty source
/// census, no second DNS listener) and the run after one connection and one datagram went through the
/// real listeners. The connection and the datagram are driven through the production paths -- the accept
/// loop with <c>TcpConnectionProtocol</c>, the receive loop with <c>SourceCensus</c> -- because those are
/// what fill the records the test reads; the run-end summaries are then written by the runner itself.
/// </remarks>
public sealed class LedgerShapeTests
{
    /// <summary>A label no key of the ledger's own is: the envelope carries it into every record.</summary>
    private const string Label = "shape";

    private const int Workers = 2;

    private const uint ConnectionId = 0x5348_0001u;

    private const int PayloadBytes = 32;

    // The record kinds as the writers spell them. They are values, not keys (D14.16), so no constant
    // declares them; the shape test reads them off the bytes it published.
    private const string TcpKind = "tcp";
    private const string TcpSummaryKind = "tcpSummary";
    private const string UdpSummaryKind = "udpSummary";
    private const string DnsSummaryKind = "dnsSummary";
    private const string TargetSummaryKind = "targetSummary";

    // Long enough that the periodic flush never fires inside a test; a memory stream needs no
    // draining for its bytes to be readable right after the write.
    private static readonly TimeSpan s_noFlush = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task EveryLedgerRecordPublishesExactlyTheDeclaredKeys()
    {
        var ledger = await PublishLedgerAsync();
        var failures = new List<string>();

        foreach (var (kind, declared) in DeclaredPaths())
        {
            if (!ledger.TryGetValue(kind, out var records))
            {
                failures.Add($"{kind}: the ledger has no record of this kind");
                continue;
            }

            // The union over the kind's records: every path the family can write, whatever state the
            // run was in when it wrote it.
            failures.AddRange(DeclaredKeys.Differences(kind, declared, [.. JsonPaths.FlattenJsonl(Join(records)).Keys]));
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// Each record writes its own keys in the order <c>ArmKeys.Ledger</c> declares them. The comparison
    /// is per record and restricted to the paths that record wrote: a key is absent from a record only
    /// through a declared condition, which the other facts cover, while a swap between two keys is only
    /// visible here.
    /// </summary>
    [Fact]
    public async Task EveryLedgerRecordWritesItsKeysInDeclarationOrder()
    {
        var ledger = await PublishLedgerAsync();
        var failures = new List<string>();

        foreach (var (kind, declared) in DeclaredPaths())
        {
            foreach (var record in ledger[kind])
            {
                var written = JsonPaths.FlattenJsonl(record).Keys.ToArray();
                var expected = declared.Where(written.Contains).ToArray();
                if (!expected.SequenceEqual(written, StringComparer.Ordinal))
                {
                    failures.Add(
                        $"{kind}: a record writes its keys in a different order than the declaration "
                        + $"(written: {string.Join(", ", written)}; declared: {string.Join(", ", expected)})");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>
    /// The three states of a ledger counter. Nothing is ever published as JSON <see langword="null"/>:
    /// a quantity with nothing behind it is written as <c>0</c> and keeps its key, so a missing key can
    /// only mean "this record never carries it", which the conditional fact covers. The first record of
    /// each kind is the state before any traffic, the last is the state after it.
    /// </summary>
    [Fact]
    public async Task AZeroCounterKeepsItsKeyAndNoLedgerValueIsNull()
    {
        var ledger = await PublishLedgerAsync();

        var nulls = new List<string>();
        foreach (var (kind, records) in ledger)
        {
            foreach (var (path, observations) in JsonPaths.FlattenJsonl(Join(records)))
            {
                if (observations[0].Kind == JsonPaths.KindNull)
                {
                    nulls.Add($"{kind} {path}");
                }
            }
        }

        Assert.True(nulls.Count == 0, $"the ledger published JSON null for: {string.Join(", ", nulls)}");

        // The state before any traffic: the counters are present and zero, not missing.
        var emptyTcp = JsonPaths.FlattenJsonl(First(ledger, TcpSummaryKind));
        Assert.Equal(0, Number(emptyTcp, ArmKeys.Ledger.TcpSummary.Connections));
        Assert.Equal(0, Number(emptyTcp, $"{ArmKeys.Ledger.TcpSummary.Verdicts}/{ArmKeys.Ledger.VerdictNames.Clean}"));

        var emptyUdp = JsonPaths.FlattenJsonl(First(ledger, UdpSummaryKind));
        Assert.Equal(0, Number(emptyUdp, ArmKeys.Ledger.UdpSummary.Received));
        Assert.Equal(0, Number(emptyUdp, ArmKeys.Ledger.UdpSummary.SourceOverflow));

        var emptyDns = JsonPaths.FlattenJsonl(First(ledger, DnsSummaryKind));
        Assert.Equal(0, Number(emptyDns, ArmKeys.Ledger.DnsSummary.UdpQueries));

        var emptyTarget = JsonPaths.FlattenJsonl(First(ledger, TargetSummaryKind));
        Assert.Equal(0, Number(emptyTarget, ArmKeys.Ledger.TargetSummary.LedgerWriteErrors));

        // The same keys carry what the measured state put through them. The connection asked for no
        // echo and got none, so the per-connection record's zeros are values as well.
        var connection = JsonPaths.FlattenJsonl(First(ledger, TcpKind));
        Assert.Equal(0, Number(connection, ArmKeys.Ledger.TcpRecord.ExpectedBytes));
        Assert.Equal(0, Number(connection, ArmKeys.Ledger.TcpRecord.BytesEchoed));

        var measuredTcp = JsonPaths.FlattenJsonl(Last(ledger, TcpSummaryKind));
        Assert.Equal(1, Number(measuredTcp, ArmKeys.Ledger.TcpSummary.Connections));
        Assert.Equal(1, Number(measuredTcp, $"{ArmKeys.Ledger.TcpSummary.Verdicts}/{ArmKeys.Ledger.VerdictNames.Clean}"));

        var measuredUdp = JsonPaths.FlattenJsonl(Last(ledger, UdpSummaryKind));
        Assert.True(Number(measuredUdp, ArmKeys.Ledger.UdpSummary.Received) >= 1, "the measured state received no datagram");
    }

    /// <summary>
    /// The source census is an array whose members are the sources an interval saw: the array itself is
    /// always written -- an interval with no datagram publishes <c>[]</c> -- while its elements exist
    /// only once a datagram arrived from one, which is why the element keys are covered by a real
    /// datagram through the receive loop rather than by a hand-built list.
    /// </summary>
    [Fact]
    public async Task TheSourceCensusIsAlwaysWrittenAndItsElementsOnlyWhenASourceArrived()
    {
        var ledger = await PublishLedgerAsync();

        var empty = JsonPaths.FlattenJsonl(First(ledger, UdpSummaryKind));
        Assert.True(empty.ContainsKey(ArmKeys.Ledger.UdpSummary.Sources), "an interval with no traffic publishes no sources key");
        Assert.Equal(0, empty[ArmKeys.Ledger.UdpSummary.Sources][0].ArrayLength);
        Assert.DoesNotContain(
            empty.Keys,
            path => path.StartsWith($"{ArmKeys.Ledger.UdpSummary.Sources}/", StringComparison.Ordinal));

        var measured = JsonPaths.FlattenJsonl(Last(ledger, UdpSummaryKind));
        var sources = measured[ArmKeys.Ledger.UdpSummary.Sources][0];
        Assert.Equal(JsonPaths.KindArray, sources.Kind);
        Assert.True(sources.ArrayLength >= 1, "the interval that saw a datagram published no source");
        Assert.Equal(
            DeclaredKeys.Under(typeof(ArmKeys.Ledger.UdpSummary.SourceEntry), ArmKeys.Ledger.UdpSummary.Sources),
            JsonPaths.Under(measured, ArmKeys.Ledger.UdpSummary.Sources));
    }

    /// <summary>
    /// The one conditional container of the ledger: a target started without a second DNS port publishes
    /// no key of <c>targetSummary/dnsAlt</c>, and one started with it publishes the same block its first
    /// listener does, under both containers, with the second listener's port. Each listener also
    /// publishes a <c>dnsSummary</c> record of its own, so the container and the record come and go
    /// together.
    /// </summary>
    [Fact]
    public async Task TheDnsAltBlockIsOmittedWholeAndTheSameDnsBlockServesBothContainers()
    {
        var ledger = await PublishLedgerAsync();
        const string dns = ArmKeys.Ledger.TargetSummary.Dns;
        const string dnsAlt = ArmKeys.Ledger.TargetSummary.DnsAlt;

        var without = JsonPaths.FlattenJsonl(First(ledger, TargetSummaryKind));
        Assert.DoesNotContain(dnsAlt, without.Keys);
        Assert.DoesNotContain(without.Keys, path => path.StartsWith($"{dnsAlt}/", StringComparison.Ordinal));

        var with = JsonPaths.FlattenJsonl(Last(ledger, TargetSummaryKind));
        Assert.Contains(dnsAlt, with.Keys);

        var first = JsonPaths.Under(with, dns);
        Assert.Equal(
            first.Select(path => $"{dnsAlt}{path[dns.Length..]}"),
            JsonPaths.Under(with, dnsAlt));

        // One dnsSummary record per listener per summary write: the empty state ran one listener, the
        // measured state ran two, and each block under targetSummary names the port its listener
        // published. The empty state is what fixes which record is the first listener's.
        var records = ledger[DnsSummaryKind];
        Assert.Equal(3, records.Count);
        var ports = records
            .Select(record => (int)Number(JsonPaths.FlattenJsonl(record), ArmKeys.Ledger.DnsSummary.Port))
            .ToArray();
        Assert.Equal(ports[0], ports[1]);
        Assert.NotEqual(ports[1], ports[2]);
        Assert.Equal(ports[1], Number(with, $"{dns}/{ArmKeys.Ledger.TargetSummary.DnsTotals.Port}"));
        Assert.Equal(ports[2], Number(with, $"{dnsAlt}/{ArmKeys.Ledger.TargetSummary.DnsTotals.Port}"));
    }

    /// <summary>
    /// The verdict names <c>ArmKeys.Ledger</c> declares are the ones the writer publishes: the writer
    /// loops <c>TcpVerdict</c> and spells each name through <c>TcpCommand.Name</c>, so a renamed verdict
    /// would otherwise move the block without moving the key.
    /// </summary>
    [Fact]
    public void TheDeclaredVerdictNamesAreThePublishedOnes()
    {
        Assert.Equal(
            DeclaredKeys.Under(typeof(ArmKeys.Ledger.VerdictNames), string.Empty),
            Enum.GetValues<TcpVerdict>().Select(TcpCommand.Name));
    }

    /// <summary>
    /// The paths the ledger declares for one record kind, in write order, read off <c>ArmKeys.Ledger</c>.
    /// The record root's own members are named one by one because the envelope and <c>type</c> sit
    /// beside them; a nested block is spread where its container key is written, so the list is the
    /// order the bytes arrive in.
    /// </summary>
    private static Dictionary<string, List<string>> DeclaredPaths() => new(StringComparer.Ordinal)
    {
        [TcpKind] = Root(
        [
            ArmKeys.Ledger.TcpRecord.ConnectionId,
            ArmKeys.Ledger.TcpRecord.Mode,
            ArmKeys.Ledger.TcpRecord.ExpectedBytes,
            ArmKeys.Ledger.TcpRecord.BytesEchoed,
            ArmKeys.Ledger.TcpRecord.Verdict,
            ArmKeys.Ledger.TcpRecord.Peer,
            ArmKeys.Ledger.TcpRecord.StartedTicks,
            ArmKeys.Ledger.TcpRecord.EndedTicks,
        ]),
        [TcpSummaryKind] = Root(
        [
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.TcpSummary), string.Empty),
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.VerdictNames), ArmKeys.Ledger.TcpSummary.Verdicts),
        ]),
        [UdpSummaryKind] = Root(
        [
            ArmKeys.Ledger.UdpSummary.Received,
            ArmKeys.Ledger.UdpSummary.Undecodable,
            ArmKeys.Ledger.UdpSummary.Bytes,
            ArmKeys.Ledger.UdpSummary.Ticks,
            ArmKeys.Ledger.UdpSummary.Sources,
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.UdpSummary.SourceEntry), ArmKeys.Ledger.UdpSummary.Sources),
            ArmKeys.Ledger.UdpSummary.SourceOverflow,
        ]),
        [DnsSummaryKind] = Root(DeclaredKeys.Under(typeof(ArmKeys.Ledger.DnsSummary), string.Empty)),
        [TargetSummaryKind] = Root(
        [
            ArmKeys.Ledger.TargetSummary.StartedTicks,
            ArmKeys.Ledger.TargetSummary.EndedTicks,
            ArmKeys.Ledger.TargetSummary.LedgerWriteErrors,
            ArmKeys.Ledger.TargetSummary.Tcp,
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.TargetSummary.TcpTotals), ArmKeys.Ledger.TargetSummary.Tcp),
            .. DeclaredKeys.Under(
                typeof(ArmKeys.Ledger.VerdictNames),
                $"{ArmKeys.Ledger.TargetSummary.Tcp}/{ArmKeys.Ledger.TargetSummary.TcpTotals.Verdicts}"),
            ArmKeys.Ledger.TargetSummary.Udp,
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.TargetSummary.UdpTotals), ArmKeys.Ledger.TargetSummary.Udp),
            ArmKeys.Ledger.TargetSummary.Dns,
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.TargetSummary.DnsTotals), ArmKeys.Ledger.TargetSummary.Dns),
            ArmKeys.Ledger.TargetSummary.DnsAlt,
            .. DeclaredKeys.Under(typeof(ArmKeys.Ledger.TargetSummary.DnsTotals), ArmKeys.Ledger.TargetSummary.DnsAlt),
        ]),
    };

    /// <summary>One record kind's declared paths, after the envelope and <c>type</c> every record opens with.</summary>
    private static List<string> Root(IEnumerable<string> body) =>
        [ArmKeys.Ledger.Envelope.Utc, ArmKeys.Ledger.Envelope.Label, ArmKeys.Common.Record.Type, .. body];

    /// <summary>
    /// The ledger one target run publishes, through the production writers: the summaries before any
    /// traffic, one connection and one datagram through the real listeners, then the run-end summaries
    /// with the second DNS listener. The result is keyed by the record kind the writer published.
    /// </summary>
    private static async Task<Dictionary<string, List<string>>> PublishLedgerAsync()
    {
        using var stream = new MemoryStream();
        await using var ledger = new JsonlSink(stream, JsonlPolicy.SwallowAndCount, TargetRunner.WriteLedgerEnvelope(Label), s_noFlush);

        var tcpPort = FreePort(SocketType.Stream, ProtocolType.Tcp);
        var udpPort = FreePort(SocketType.Dgram, ProtocolType.Udp);
        var dnsPort = FreeDnsPort();
        var dnsAltPort = FreeDnsPort();

        await using var tcp = new TcpTargetServer(new IPEndPoint(IPAddress.Loopback, tcpPort), ledger);
        await using var udp = new UdpEchoServer(new IPEndPoint(IPAddress.Loopback, udpPort), ledger, Workers);
        await using var dns = new DnsServer(new IPEndPoint(IPAddress.Loopback, dnsPort), ledger, Workers);
        await using var dnsAlt = new DnsServer(new IPEndPoint(IPAddress.Loopback, dnsAltPort), ledger, Workers);

        // The state a reader sees before anything happened: every counter zero, no source in the census,
        // and no second DNS listener, whose whole block is conditional on the port.
        await TargetRunner.WriteSummariesAsync(ledger, tcp, udp, dns, dnsAlt: null, startedTicks: 0, endedTicks: 0);

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var accepting = tcp.RunAsync(shutdown.Token);
        var receiving = udp.RunAsync(shutdown.Token);
        await RunOneConnectionAsync(tcpPort, shutdown.Token);
        await EchoOneDatagramAsync(udpPort, shutdown.Token);
        await shutdown.CancelAsync();
        await Task.WhenAll(accepting, receiving);

        // The state the same target publishes at the end of a run that did serve both listeners.
        await TargetRunner.WriteSummariesAsync(ledger, tcp, udp, dns, dnsAlt, startedTicks: 3, endedTicks: 4);
        await ledger.CompleteAsync();

        var records = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in Encoding.UTF8.GetString(stream.ToArray()).Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var kind = document.RootElement.GetProperty(ArmKeys.Common.Record.Type).GetString()!;
            if (!records.TryGetValue(kind, out var found))
            {
                found = [];
                records.Add(kind, found);
            }

            found.Add(line);
        }

        return records;
    }

    /// <summary>
    /// One connection through the real listener: the command frame the arm would send, then a half-close,
    /// which is the mode's contract for a clean verdict. The record it leaves is written by the
    /// connection path, and the accept loop's drain has joined it before this returns.
    /// </summary>
    private static async Task RunOneConnectionAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);
        await SocketOps.SendCommandAsync(client, ConnectionId, TcpMode.Clean, expectedBytes: 0, cancellationToken).ConfigureAwait(false);
        SocketOps.ShutdownQuietly(client, SocketShutdown.Send);

        var buffer = new byte[PayloadBytes];
        int read;
        do
        {
            read = await client.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        }
        while (read > 0);

        Assert.Equal(0, read);
    }

    /// <summary>
    /// One datagram through the real receive loop: a valid frame is echoed back, and the echo proves the
    /// census recorded its source before the summary that reports the source is written.
    /// </summary>
    private static async Task EchoOneDatagramAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);

        var frame = new FrameBuffer(PayloadBytes);
        var length = frame.Build(ConnectionId, sequence: 1, sendTicks: 0);
        var sent = frame.Memory[..length].ToArray();
        await client.SendAsync(sent, SocketFlags.None, cancellationToken).ConfigureAwait(false);

        var echoed = new byte[sent.Length];
        var received = await client.ReceiveAsync(echoed, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        Assert.Equal(sent.Length, received);
        Assert.Equal(sent, echoed);
    }

    /// <summary>The first record of one kind: the state before any traffic went through the listeners.</summary>
    private static string First(Dictionary<string, List<string>> ledger, string kind) => ledger[kind][0];

    /// <summary>The last record of one kind: the state the run ended in.</summary>
    private static string Last(Dictionary<string, List<string>> ledger, string kind) => ledger[kind][^1];

    private static string Join(IEnumerable<string> records) => string.Join('\n', records);

    /// <summary>The number one path carries in a flattened record.</summary>
    private static double Number(Dictionary<string, List<JsonPathObservation>> record, string path) =>
        record[path][0].Value.GetDouble();

    /// <summary>
    /// A port nothing holds right now: the probe is bound to port 0 so the kernel picks one, and it is
    /// released before the caller binds it for real.
    /// </summary>
    private static int FreePort(SocketType socketType, ProtocolType protocolType)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, socketType, protocolType);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>
    /// A port free for both transports, which the DNS responder needs: it binds a datagram socket and a
    /// stream listener on the same number, so a port free for one of them is not enough.
    /// </summary>
    private static int FreeDnsPort()
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var port = FreePort(SocketType.Dgram, ProtocolType.Udp);
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException)
            {
                /* another process holds the stream half of this number; the next probe picks another */
            }
        }

        throw new InvalidOperationException("no port was free for both a datagram socket and a stream listener");
    }
}
