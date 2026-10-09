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
/// The shape contract of the target's ledger: the bytes the record families publish, flattened with
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
public sealed partial class LedgerShapeTests
{
    /// <summary>A label no key of the ledger's own is: the envelope carries it into every record.</summary>
    private const string Label = "shape";

    private const int Workers = 2;

    private const uint ConnectionId = 0x5348_0001u;

    private const int PayloadBytes = 32;

    // The record kinds as the writers spell them. They are values, not keys, so no constant
    // declares them; the shape test reads them off the bytes it published.
    private const string TcpKind = "tcp";
    private const string TcpSummaryKind = "tcpSummary";
    private const string UdpSummaryKind = "udpSummary";
    private const string DnsSummaryKind = "dnsSummary";
    private const string TargetSummaryKind = "targetSummary";
    private const string ErrorKind = "error";

    /// <summary>The receive loops a listener starts in these facts, which is not the default formula.</summary>
    private const int Receivers = 13;

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
        Assert.Equal(0, Number(emptyTcp, ArmKeys.Ledger.TcpSummary.TruncatedFrames));
        Assert.Equal(0, Number(emptyTcp, ArmKeys.Ledger.TcpSummary.AcceptErrors));
        Assert.Equal(0, Number(emptyTcp, $"{ArmKeys.Ledger.TcpSummary.Verdicts}/{ArmKeys.Ledger.VerdictNames.Clean}"));

        var emptyUdp = JsonPaths.FlattenJsonl(First(ledger, UdpSummaryKind));
        Assert.Equal(0, Number(emptyUdp, ArmKeys.Ledger.UdpSummary.Received));
        Assert.Equal(0, Number(emptyUdp, ArmKeys.Ledger.UdpSummary.SourceOverflow));

        var emptyDns = JsonPaths.FlattenJsonl(First(ledger, DnsSummaryKind));
        Assert.Equal(0, Number(emptyDns, ArmKeys.Ledger.DnsSummary.UdpQueries));
        Assert.Equal(0, Number(emptyDns, ArmKeys.Ledger.DnsSummary.TruncatedFrames));
        Assert.Equal(0, Number(emptyDns, ArmKeys.Ledger.DnsSummary.AcceptErrors));

        var emptyTarget = JsonPaths.FlattenJsonl(First(ledger, TargetSummaryKind));
        Assert.Equal(0, Number(emptyTarget, ArmKeys.Ledger.TargetSummary.LedgerWriteErrors));

        // The listener's concurrency is a counter of the loops that started, so the state before they
        // run is zero rather than the number the server was built with.
        Assert.Equal(
            0,
            Number(emptyTarget, $"{ArmKeys.Ledger.TargetSummary.Udp}/{ArmKeys.Ledger.TargetSummary.UdpTotals.UdpReceivers}"));

        // The same keys carry what the measured state put through them. The connection asked for no
        // echo and got none, so the per-connection record's zeros are values as well.
        var connection = JsonPaths.FlattenJsonl(First(ledger, TcpKind));
        Assert.Equal(0, Number(connection, ArmKeys.Ledger.TcpRecord.ExpectedBytes));
        Assert.Equal(0, Number(connection, ArmKeys.Ledger.TcpRecord.BytesEchoed));

        var measuredTcp = JsonPaths.FlattenJsonl(Last(ledger, TcpSummaryKind));
        Assert.Equal(2, Number(measuredTcp, ArmKeys.Ledger.TcpSummary.Connections));
        Assert.Equal(1, Number(measuredTcp, ArmKeys.Ledger.TcpSummary.TruncatedFrames));
        Assert.Equal(1, Number(measuredTcp, $"{ArmKeys.Ledger.TcpSummary.Verdicts}/{ArmKeys.Ledger.VerdictNames.Clean}"));
        Assert.Equal(1, Number(measuredTcp, $"{ArmKeys.Ledger.TcpSummary.Verdicts}/{ArmKeys.Ledger.VerdictNames.ProtocolError}"));

        // A run whose listeners were never refused an accept keeps the key at zero, which is what
        // separates "nothing was refused" from "the count is not published".
        Assert.Equal(0, Number(measuredTcp, ArmKeys.Ledger.TcpSummary.AcceptErrors));

        var measuredDns = JsonPaths.FlattenJsonl(ledger[DnsSummaryKind][1]);
        Assert.Equal(1, Number(measuredDns, ArmKeys.Ledger.DnsSummary.TruncatedFrames));
        Assert.Equal(0, Number(measuredDns, ArmKeys.Ledger.DnsSummary.AcceptErrors));
        Assert.Equal(0, Number(measuredDns, ArmKeys.Ledger.DnsSummary.TcpAborted));

        var measuredUdp = JsonPaths.FlattenJsonl(Last(ledger, UdpSummaryKind));
        Assert.True(Number(measuredUdp, ArmKeys.Ledger.UdpSummary.Received) >= 1, "the measured state received no datagram");

        var measuredTarget = JsonPaths.FlattenJsonl(Last(ledger, TargetSummaryKind));
        Assert.Equal(
            Workers,
            Number(measuredTarget, $"{ArmKeys.Ledger.TargetSummary.Udp}/{ArmKeys.Ledger.TargetSummary.UdpTotals.UdpReceivers}"));
    }

    /// <summary>
    /// The truncation counters reach the ledger the way every other total does -- at the summary's own
    /// root and one level down under <c>targetSummary</c> -- and each listener counts its own stream.
    /// The TCP listener counts the connection whose close landed inside a frame; a DNS listener counts
    /// a length-prefixed read that ended short, which is a different mechanism under the same key name.
    /// The second DNS listener served nothing here, so the two blocks are also shown to be
    /// two counters rather than one written twice.
    /// </summary>
    [Fact]
    public async Task TheTruncationCountersReachBothLevelsAndEachListenerCountsItsOwn()
    {
        var ledger = await PublishLedgerAsync();
        var target = JsonPaths.FlattenJsonl(Last(ledger, TargetSummaryKind));

        var tcp = JsonPaths.FlattenJsonl(Last(ledger, TcpSummaryKind));
        Assert.Equal(1, Number(tcp, ArmKeys.Ledger.TcpSummary.TruncatedFrames));
        Assert.Equal(
            Number(tcp, ArmKeys.Ledger.TcpSummary.TruncatedFrames),
            Number(target, $"{ArmKeys.Ledger.TargetSummary.Tcp}/{ArmKeys.Ledger.TargetSummary.TcpTotals.TruncatedFrames}"));

        var first = JsonPaths.FlattenJsonl(ledger[DnsSummaryKind][1]);
        var second = JsonPaths.FlattenJsonl(ledger[DnsSummaryKind][2]);
        Assert.Equal(1, Number(first, ArmKeys.Ledger.DnsSummary.TruncatedFrames));
        Assert.Equal(0, Number(second, ArmKeys.Ledger.DnsSummary.TruncatedFrames));
        Assert.Equal(
            Number(first, ArmKeys.Ledger.DnsSummary.TruncatedFrames),
            Number(target, $"{ArmKeys.Ledger.TargetSummary.Dns}/{ArmKeys.Ledger.TargetSummary.DnsTotals.TruncatedFrames}"));
        Assert.Equal(
            Number(second, ArmKeys.Ledger.DnsSummary.TruncatedFrames),
            Number(target, $"{ArmKeys.Ledger.TargetSummary.DnsAlt}/{ArmKeys.Ledger.TargetSummary.DnsTotals.TruncatedFrames}"));
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
        [ErrorKind] = Root(DeclaredKeys.Under(typeof(ArmKeys.Ledger.ErrorRecord), string.Empty)),
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

        var tcpPort = ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp);
        var udpPort = ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp);
        var dnsPort = ArmRunFixture.FreeDualPort();
        var dnsAltPort = ArmRunFixture.FreeDualPort();

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

        // The DNS listener has to be accepting for the short stream read below.
        var serving = dns.RunAsync(shutdown.Token);
        await RunOneConnectionAsync(tcpPort, shutdown.Token);
        await ArmRunFixture.EchoOneDatagramAsync(udpPort, shutdown.Token);
        await RunOneTruncatedConnectionAsync(tcpPort, shutdown.Token);
        await RunOneShortDnsReadAsync(dnsPort, shutdown.Token);
        await shutdown.CancelAsync();
        await Task.WhenAll(accepting, receiving, serving);

        // The state the same target publishes at the end of a run that did serve both listeners.
        await TargetRunner.WriteSummariesAsync(ledger, tcp, udp, dns, dnsAlt, startedTicks: 3, endedTicks: 4);

        // And the record a failure leaves when it cannot be written as the record it belonged to: the
        // summary guard catches the body's exception and books the cause, which is the one path to the
        // ledger's error family.
        await TargetRunner.WriteSummaryAsync(ledger, UdpSummaryKind, ThrowingSummaryBody);

        await ledger.CompleteAsync();

        return Parse(stream);
    }

    /// <summary>
    /// The lines the sink wrote, keyed by the record kind the writer published. Reading the bytes back is
    /// what makes the comparison a shape comparison: the keys are the ones that reached the file.
    /// </summary>
    private static Dictionary<string, List<string>> Parse(MemoryStream stream)
    {
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
    /// A listener whose first <paramref name="refusals"/> accept calls fail with the error a full
    /// descriptor table produces, and whose later ones are real: a refused accept is the kernel's
    /// decision to time, so the count's evidence scripts the call that produces it and then lets the
    /// loop serve normally.
    /// </summary>
    private static (TcpAcceptLoop Loop, IPEndPoint EndPoint) RefusingLoop(int refusals)
    {
        var listener = Sockets.BindTcpListener(new IPEndPoint(IPAddress.Loopback, 0));
        var endPoint = (IPEndPoint)listener.LocalEndPoint!;
        var remaining = refusals;
        return (
            new TcpAcceptLoop(
                listener,
                (socket, cancellationToken) => Interlocked.Decrement(ref remaining) >= 0
                    ? throw new SocketException((int)SocketError.TooManyOpenSockets)
                    : socket.AcceptAsync(cancellationToken)),
            endPoint);
    }

    /// <summary>
    /// Waits for both loops to have booked the scripted refusals, with the bound a scheduling delay
    /// needs: the counters are written by the loops' own threads, so the fact synchronizes on them
    /// rather than on a proxy for their progress.
    /// </summary>
    private static async Task WaitForAcceptErrorsAsync(TcpAcceptLoop first, TcpAcceptLoop second)
    {
        var deadline = Environment.TickCount64 + 5000;
        while ((first.AcceptErrors < 2 || second.AcceptErrors < 2) && Environment.TickCount64 < deadline)
        {
            await Task.Yield();
        }
    }

    /// <summary>
    /// The body a summary guard is handed when its record cannot be written: an exception wrapped around
    /// the cause the ledger has to name, because a failure on this path arrives nested in practice.
    /// </summary>
    private static ValueTask ThrowingSummaryBody() =>
        throw new InvalidOperationException("the shape test's summary body failed", new SocketException((int)SocketError.ConnectionReset));

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
    /// One connection whose command frame is read whole and whose stream then stops inside the next
    /// frame: the state the truncation counters exist for. The mode is <c>halfClose</c>, whose clean
    /// close earns a trailer, so this drive is also what separates "the peer half-closed" from "the
    /// peer stopped writing".
    /// </summary>
    private static async Task RunOneTruncatedConnectionAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);
        await SocketOps.SendCommandAsync(client, ConnectionId, TcpMode.HalfClose, expectedBytes: 0, cancellationToken).ConfigureAwait(false);

        // Ten bytes of a frame: more than nothing and fewer than a header, which is the shape a
        // connection cut mid-frame arrives in.
        var frame = new FrameBuffer(PayloadBytes);
        frame.Build(ConnectionId, sequence: 1, sendTicks: 0);
        await client.SendAsync(frame.Memory[..10], SocketFlags.None, cancellationToken).ConfigureAwait(false);
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
    /// One DNS stream connection that stops between messages: the length prefix was never begun, so
    /// the listener has nothing to answer and no message was cut in half.
    /// </summary>
    private static async Task RunOneShortDnsReadAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);

        // One byte of the two-byte length prefix: the read ends short, not at a message boundary.
        await client.SendAsync(new byte[1], SocketFlags.None, cancellationToken).ConfigureAwait(false);
        SocketOps.ShutdownQuietly(client, SocketShutdown.Send);

        var buffer = new byte[PayloadBytes];
        int read;
        do
        {
            read = await client.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        }
        while (read > 0);
    }

    /// <summary>The first record of one kind: the state before any traffic went through the listeners.</summary>
    private static string First(Dictionary<string, List<string>> ledger, string kind) => ledger[kind][0];

    /// <summary>The last record of one kind: the state the run ended in.</summary>
    private static string Last(Dictionary<string, List<string>> ledger, string kind) => ledger[kind][^1];

    private static string Join(IEnumerable<string> records) => string.Join('\n', records);

    /// <summary>The number one path carries in a flattened record.</summary>
    private static double Number(Dictionary<string, List<JsonPathObservation>> record, string path) =>
        record[path][0].Value.GetDouble();
}
