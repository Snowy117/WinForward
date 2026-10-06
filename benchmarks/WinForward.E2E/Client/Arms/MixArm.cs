using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal sealed class MixCounters
{
    internal MixCounters(int desktops)
    {
        PageConnectionsPerDesktop = new long[desktops];
        BulkFramesPerDesktop = new long[desktops];
        DnsSentPerDesktop = new long[desktops];
    }

    // One witness per flow class is kept per desktop so that a lane which never ran is visible in
    // the record instead of being averaged into a total that still looks plausible. The arm-wide
    // values are summed from these, so a witness and its total cannot drift apart.
    internal long[] PageConnectionsPerDesktop { get; }

    internal long[] BulkFramesPerDesktop { get; }

    internal long[] DnsSentPerDesktop { get; }

    internal long PageConnections => Total(PageConnectionsPerDesktop);

    internal long BulkFrames => Total(BulkFramesPerDesktop);

    internal long DnsSent => Total(DnsSentPerDesktop);

    internal long _pages;
    internal long _pageMessages;
    internal long _pageBytes;
    internal long _pageErrors;
    internal long _bulkBytes;
    internal long _bulkBytesSent;
    internal long _bulkErrors;
    internal long _dnsAnswered;
    internal long _dnsServfail;
    internal long _dnsTimeout;
    internal long _dnsOther;
    internal long _udpBytes;

    private static long Total(long[] perDesktop)
    {
        long total = 0;
        foreach (var value in perDesktop)
        {
            total += value;
        }

        return total;
    }
}

internal static class MixArm
{
    /// <summary>
    /// Arm-wide UDP tallies folded from the per-desktop trackers, so the published class and the
    /// per-desktop lanes are two views of one classification pass.
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    private struct UdpTotals
    {
        internal long _sent;
        internal long _arrived;
        internal long _late;
        internal long _never;
        internal long _undetermined;
        internal long _corrupt;
        internal long _corruptDatagrams;
        internal long _duplicate;
        internal long _reordered;
        internal long _unmatched;
        internal long _foreign;
        internal long _sendFailures;
        internal long _windowOverflow;
        internal long _outOfRange;
        internal long _clientSendLoss;

        internal readonly long Loss => _late + _never;

        internal void Add(UdpReliabilityTracker tracker, LossCounts counts)
        {
            _sent += tracker.SentOk;
            _arrived += counts.Arrived;
            _late += counts.Late;
            _never += counts.Never;
            _undetermined += counts.Undetermined;
            _corrupt += counts.Corrupt;
            _corruptDatagrams += counts.CorruptDatagrams;
            _duplicate += counts.Duplicate;
            _reordered += counts.Reordered;
            _unmatched += tracker.UnmatchedReplies;
            _foreign += tracker.ForeignConnection;
            _sendFailures += tracker.SendFailure;
            _windowOverflow += tracker.WindowOverflow;
            _outOfRange += tracker.OutOfRange;
            _clientSendLoss += tracker.SendFailure + tracker.WindowOverflow + counts.Undetermined;
        }
    }

    private const int DefaultDesktops = 4;
    private const int PageConnections = 13;
    private const int PageTotalRequests = 73;
    private const int PageMessageBytes = 38_000;
    private const long BulkBitsPerSecond = 5_000_000;
    private const int BulkPayloadBytes = 32 * 1024;
    private const int DnsQueriesPerPage = 4;
    private const int UdpPacketsPerSecond = 30;
    private const int UdpPayloadBytes = 120;
    private static readonly TimeSpan s_pageInterval = TimeSpan.FromSeconds(20);

    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var desktops = context.Spec.Desktops > 0 ? context.Spec.Desktops : DefaultDesktops;
        var windowMs = context.Spec.LossWindowMs > 0 ? context.Spec.LossWindowMs : UdpLossMath.DefaultWindowMilliseconds;
        var windowTicks = UdpLossMath.WindowTicks(windowMs);
        var outcome = new ArmOutcome
        {
            Parameters =
            {
                ["seconds"] = context.Spec.Seconds,
                ["desktops"] = desktops,
                ["pageIntervalSeconds"] = s_pageInterval.TotalSeconds,
                ["pageConnections"] = PageConnections,
                ["pageRequestsTotal"] = PageTotalRequests,
                ["pageMessageBytes"] = PageMessageBytes,
                ["bulkBitsPerSecondPerDesktop"] = BulkBitsPerSecond,
                ["dnsQueriesPerPage"] = DnsQueriesPerPage,
                ["udpPacketsPerSecondPerDesktop"] = UdpPacketsPerSecond,
                ["udpPayloadBytes"] = UdpPayloadBytes,
                ["lossWindowMs"] = windowMs,
            },
        };

        var counters = new MixCounters(desktops);
        var trackers = new UdpReliabilityTracker[desktops];
        var observationEnds = new long[desktops];
        var startTicks = Clock.Now;
        var deadlineTicks = context.DeadlineTicks(startTicks);
        using var linked = context.CreateLinkedTokenSource();
        var cancellationToken = linked.Token;

        var tasks = new Task[desktops];
        for (var index = 0; index < desktops; index++)
        {
            trackers[index] = new UdpReliabilityTracker();
            var desktopIndex = index;
            // Each desktop gets a thread of its own. A lane blocks in its pacer before its first
            // genuinely asynchronous await, so starting a desktop inline would keep this loop from
            // ever constructing the next one and the arm would silently measure fewer desktops than
            // the plan declares.
            tasks[index] = Dedicated.RunOnOwnThreadAsync(() => RunDesktopAsync(context, counters, trackers[desktopIndex], observationEnds, desktopIndex, startTicks, deadlineTicks, windowTicks, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var (clientSendLoss, idleLanes) = WriteMetrics(outcome, counters, trackers, observationEnds, windowMs, windowTicks, Clock.Now - startTicks);
        outcome.Gates["clientSendLoss"] = clientSendLoss;
        outcome.Gates["idleLanes"] = idleLanes;
        outcome.Gates["windowMs"] = (double)windowMs;
        outcome.Notes.Add("W is the plan's lossWindowMs, 200 ms when the plan does not declare one: it is declared and published as classes.udp.window rather than derived, so every row's arrived/late/never split is reproducible from the record alone (RFC 2680 style).");
        outcome.Notes.Add("every desktop's UDP drain runs until W after that desktop's last real send, so no datagram is called lost before its whole W has passed; datagrams still inside their window when a drain is cut short are counted in classes.udp.abandonedAtTeardown and in clientSendLoss, never in never, and they stay out of lossRate.");
        outcome.Notes.Add("every sent datagram is classified exactly once, so arrived + late + never + abandonedAtTeardown + corruptDatagrams == sent by construction, exactly as in LOSS, and classes.udp.clientSendLoss is classes.udp.abandonedAtTeardown + classes.udp.sendFailures + classes.udp.windowOverflow with all three terms published beside it; corruptDatagrams counts the sent datagrams booked corrupt while corrupt counts corrupt arrivals, windowOverflow is structurally zero because the MIX UDP class has no in-flight window, and sendFailures counts datagrams the socket refused, which is why a refused datagram is client loss and never path loss.");
        outcome.Notes.Add("sentPerDesktop and desktops carry one witness per flow class per desktop, and gates.idleLanes counts the witnesses that stayed at zero: a lane that never ran is a harness failure, not a smaller aggregate.");
        outcome.Notes.Add("reordered counts arrivals that follow the arrival of a higher sequence, the RFC 4737 definition; foreignConnection counts replies carrying another flow's connection id, so a non-zero value means the product mixed datagrams between flows. classes.udp.outOfRangeSequences counts sequences the tracker refused as outside its bounded sequence space: a non-zero value means part of the offered schedule was never tracked, so the classification covers fewer datagrams than sent.");
        outcome.Notes.Add("every flow class carries the shared frame layout, so the per-class byte and latency numbers come from the same measurement path as the dedicated arms.");
        outcome.Notes.Add("a rate whose denominator is zero is written as null rather than 0: nothing was sent, so there is no rate to report.");
        return outcome;
    }

    private static (long ClientSendLoss, int IdleLanes) WriteMetrics(
        ArmOutcome outcome,
        MixCounters counters,
        UdpReliabilityTracker[] trackers,
        long[] observationEnds,
        int windowMs,
        long windowTicks,
        long elapsedTicks)
    {
        var counts = new LossCounts[trackers.Length];
        var sentPerDesktop = new long[trackers.Length];
        var udp = ClassifyDesktops(trackers, observationEnds, windowTicks, counts, sentPerDesktop);

        outcome.Metrics["classes"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["page"] = BuildPageClass(counters),
            ["bulk"] = BuildBulkClass(counters, elapsedTicks),
            ["dns"] = BuildDnsClass(counters),
            ["udp"] = BuildUdpClass(counters, udp, sentPerDesktop, windowMs),
        };
        outcome.Metrics["pages"] = counters._pages;
        outcome.Metrics["pageConnections"] = counters.PageConnections;
        outcome.Metrics["pageBytes"] = counters._pageBytes;
        outcome.Metrics["bulkBytes"] = counters._bulkBytes;
        outcome.Metrics["dnsSent"] = counters.DnsSent;
        outcome.Metrics["udpSent"] = udp._sent;
        outcome.Metrics["udpLossRate"] = JsonValue.Ratio(udp.Loss, udp._sent);
        outcome.Metrics["clientSendLoss"] = udp._clientSendLoss;
        outcome.Metrics["desktops"] = BuildDesktopLanes(counters, trackers, counts, sentPerDesktop);
        return (udp._clientSendLoss, CountIdleLanes(counters, sentPerDesktop));
    }

    private static UdpTotals ClassifyDesktops(
        UdpReliabilityTracker[] trackers,
        long[] observationEnds,
        long windowTicks,
        LossCounts[] counts,
        long[] sentPerDesktop)
    {
        var totals = default(UdpTotals);
        for (var desktop = 0; desktop < trackers.Length; desktop++)
        {
            var tracker = trackers[desktop];
            var classified = tracker.Classify(windowTicks, observationEnds[desktop]);
            counts[desktop] = classified;
            sentPerDesktop[desktop] = tracker.SentOk;
            totals.Add(tracker, classified);
        }

        return totals;
    }

    private static Dictionary<string, object?> BuildUdpClass(MixCounters counters, UdpTotals totals, long[] sentPerDesktop, int windowMs) => new(StringComparer.Ordinal)
    {
        ["sent"] = totals._sent,
        ["arrived"] = totals._arrived,
        ["late"] = totals._late,
        ["never"] = totals._never,
        ["corrupt"] = totals._corrupt,
        ["corruptDatagrams"] = totals._corruptDatagrams,
        ["duplicate"] = totals._duplicate,
        ["reordered"] = totals._reordered,
        ["unmatchedReplies"] = totals._unmatched,
        ["foreignConnection"] = totals._foreign,
        ["abandonedAtTeardown"] = totals._undetermined,
        ["sendFailures"] = totals._sendFailures,
        ["windowOverflow"] = totals._windowOverflow,
        ["outOfRangeSequences"] = totals._outOfRange,
        ["bytes"] = counters._udpBytes,
        ["clientSendLoss"] = totals._clientSendLoss,
        ["lossRate"] = JsonValue.Ratio(totals.Loss, totals._sent),
        ["window"] = (double)windowMs,
        ["sentPerDesktop"] = sentPerDesktop,
    };

    private static int CountIdleLanes(MixCounters counters, long[] sentPerDesktop)
    {
        var idle = 0;
        for (var desktop = 0; desktop < sentPerDesktop.Length; desktop++)
        {
            idle += counters.PageConnectionsPerDesktop[desktop] == 0 ? 1 : 0;
            idle += counters.BulkFramesPerDesktop[desktop] == 0 ? 1 : 0;
            idle += counters.DnsSentPerDesktop[desktop] == 0 ? 1 : 0;
            idle += sentPerDesktop[desktop] == 0 ? 1 : 0;
        }

        return idle;
    }

    private static List<object?> BuildDesktopLanes(MixCounters counters, UdpReliabilityTracker[] trackers, LossCounts[] counts, long[] sentPerDesktop)
    {
        var lanes = new List<object?>(trackers.Length);
        for (var desktop = 0; desktop < trackers.Length; desktop++)
        {
            lanes.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["desktop"] = desktop,
                ["udpSent"] = sentPerDesktop[desktop],
                ["udpArrived"] = counts[desktop].Arrived,
                ["udpNever"] = counts[desktop].Never,
                ["udpForeignConnection"] = trackers[desktop].ForeignConnection,
                ["pageConnections"] = counters.PageConnectionsPerDesktop[desktop],
                ["bulkFrames"] = counters.BulkFramesPerDesktop[desktop],
                ["dnsSent"] = counters.DnsSentPerDesktop[desktop],
            });
        }

        return lanes;
    }

    private static Dictionary<string, object?> BuildPageClass(MixCounters counters) => new(StringComparer.Ordinal)
    {
        ["pages"] = counters._pages,
        ["connections"] = counters.PageConnections,
        ["messages"] = counters._pageMessages,
        ["bytes"] = counters._pageBytes,
        ["errors"] = counters._pageErrors,
        ["bytesPerPage"] = counters._pages == 0 ? null : counters._pageBytes / counters._pages,
    };

    private static Dictionary<string, object?> BuildBulkClass(MixCounters counters, long elapsedTicks) => new(StringComparer.Ordinal)
    {
        ["bytes"] = counters._bulkBytes,
        ["bytesSent"] = counters._bulkBytesSent,
        ["frames"] = counters.BulkFrames,
        ["errors"] = counters._bulkErrors,
        ["goodputBps"] = JsonValue.Round(counters._bulkBytes / Clock.ToSeconds(elapsedTicks)),
    };

    private static Dictionary<string, object?> BuildDnsClass(MixCounters counters) => new(StringComparer.Ordinal)
    {
        ["sent"] = counters.DnsSent,
        ["answered"] = counters._dnsAnswered,
        ["servfail"] = counters._dnsServfail,
        ["timeout"] = counters._dnsTimeout,
        ["other"] = counters._dnsOther,
    };

    private static async Task RunDesktopAsync(
        ArmContext context,
        MixCounters counters,
        UdpReliabilityTracker tracker,
        long[] observationEnds,
        int desktopIndex,
        long startTicks,
        long deadlineTicks,
        long windowTicks,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        // Each lane gets its own thread for the same reason the desktops do: array elements are
        // evaluated in order, so a lane that blocks before yielding would keep the others from ever
        // being started.
        var tasks = new[]
        {
            Dedicated.RunOnOwnThreadAsync(() => PageLoopAsync(context, counters, desktopIndex, startTicks, deadlineTicks, token)),
            Dedicated.RunOnOwnThreadAsync(() => BulkLoopAsync(context, counters, desktopIndex, startTicks, deadlineTicks, token)),
            Dedicated.RunOnOwnThreadAsync(() => UdpLoopAsync(context, counters, tracker, observationEnds, desktopIndex, startTicks, deadlineTicks, windowTicks, token)),
        };

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task PageLoopAsync(
        ArmContext context,
        MixCounters counters,
        int desktopIndex,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        var pacer = new Pacer(1.0 / s_pageInterval.TotalSeconds, startTicks);
        long pageIndex = 0;
        try
        {
            while (Clock.Now < deadlineTicks)
            {
                var intended = pacer.IntendedTicks(pageIndex);
                if (intended >= deadlineTicks)
                {
                    return;
                }

                if (pageIndex > 0)
                {
                    await Pacer.WaitUntilAsync(intended, cancellationToken).ConfigureAwait(false);
                }

                var page = new Task[PageConnections];
                for (var connection = 0; connection < PageConnections; connection++)
                {
                    page[connection] = PageConnectionAsync(context, counters, desktopIndex, connection, intended, cancellationToken);
                }

                await Task.WhenAll(page).ConfigureAwait(false);
                Interlocked.Increment(ref counters._pages);
                await PageDnsAsync(context, counters, desktopIndex, pageIndex, cancellationToken).ConfigureAwait(false);
                pageIndex++;
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
    }

    private static int RequestsForConnection(int connectionIndex)
    {
        const int baseRequests = PageTotalRequests / PageConnections;
        const int remainder = PageTotalRequests % PageConnections;
        return connectionIndex < remainder ? baseRequests + 1 : baseRequests;
    }

    private static async Task PageConnectionAsync(
        ArmContext context,
        MixCounters counters,
        int desktopIndex,
        int connectionIndex,
        long intendedTicks,
        CancellationToken cancellationToken)
    {
        using var socket = context.CreateTcpSocket();
        if (!await SocketOps.TryConnectAsync(socket, context.TcpEndPoint, cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref counters._pageErrors);
            return;
        }

        Interlocked.Increment(ref counters.PageConnectionsPerDesktop[desktopIndex]);
        context.Latency.TcpConnect.Record(Clock.ToNanoseconds(Clock.Now - intendedTicks));

        var frame = new FrameBuffer(PageMessageBytes);
        var reader = new FrameStreamReader(socket, 128 * 1024);
        var connectionId = 0x4D49_0000u + (uint)(desktopIndex << 8) + (uint)connectionIndex;
        var requests = RequestsForConnection(connectionIndex);
        ulong sequence = 0;

        try
        {
            await SocketOps.SendCommandAsync(socket, connectionId, TcpMode.Clean, 0, cancellationToken).ConfigureAwait(false);
            for (var request = 0; request < requests; request++)
            {
                var requestIntended = Clock.Now;
                var length = frame.Build(connectionId, ++sequence, requestIntended);
                await socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref counters._pageBytes, length);

                var status = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (status != FrameReadStatus.Frame)
                {
                    Interlocked.Increment(ref counters._pageErrors);
                    return;
                }

                Interlocked.Increment(ref counters._pageMessages);
                context.Latency.TcpRtt.Record(Clock.ToNanoseconds(Clock.Now - requestIntended));
            }

            SocketOps.ShutdownQuietly(socket, SocketShutdown.Send);
        }
        catch (SocketException)
        {
            Interlocked.Increment(ref counters._pageErrors);
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Increment(ref counters._pageErrors);
        }
    }

    private static async Task PageDnsAsync(
        ArmContext context,
        MixCounters counters,
        int desktopIndex,
        long pageIndex,
        CancellationToken cancellationToken)
    {
        using var socket = context.CreateUdpSocket();
        await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
        var sendBuffer = new byte[512];
        var receiveBuffer = new byte[4096];

        try
        {
            for (var query = 0; query < DnsQueriesPerPage; query++)
            {
                var index = (pageIndex * DnsQueriesPerPage) + query;
                var transactionId = (ushort)((desktopIndex * 1024) + index + 1);
                await SendPageDnsQueryAsync(context, socket, counters, sendBuffer, receiveBuffer, desktopIndex, index, transactionId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SocketException)
        {
            Interlocked.Increment(ref counters._dnsOther);
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }
    }

    private static async Task SendPageDnsQueryAsync(
        ArmContext context,
        Socket socket,
        MixCounters counters,
        byte[] sendBuffer,
        byte[] receiveBuffer,
        int desktopIndex,
        long index,
        ushort transactionId,
        CancellationToken cancellationToken)
    {
        var queryType = (index % 4) switch
        {
            0 => DnsWire.TypeA,
            1 => DnsWire.TypeAaaa,
            2 => DnsWire.TypeHttps,
            _ => DnsWire.TypeTxt,
        };

        var length = DnsWire.BuildQuery(
            sendBuffer,
            transactionId,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"p{index}.bench.local"),
            queryType);
        if (length <= 0)
        {
            Interlocked.Increment(ref counters._dnsOther);
            return;
        }

        var intendedTicks = Clock.Now;
        await socket.SendAsync(sendBuffer.AsMemory(0, length), SocketFlags.None, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref counters.DnsSentPerDesktop[desktopIndex]);

        if (!await WaitForResponseAsync(socket, receiveBuffer, cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref counters._dnsTimeout);
            return;
        }

        context.Latency.DnsRtt.Record(Clock.ToNanoseconds(Clock.Now - intendedTicks));
        if (!DnsWire.TryParseResponse(receiveBuffer, out var responseId, out var rcode, out _) || responseId != transactionId)
        {
            Interlocked.Increment(ref counters._dnsOther);
            return;
        }

        if (rcode == 0)
        {
            Interlocked.Increment(ref counters._dnsAnswered);
        }
        else
        {
            Interlocked.Increment(ref counters._dnsServfail);
        }
    }

    private static async Task<bool> WaitForResponseAsync(Socket socket, byte[] receiveBuffer, CancellationToken cancellationToken)
    {
        var deadline = Clock.Now + Clock.FromSeconds(1);
        while (Clock.Now < deadline)
        {
            if (socket.Available > 0)
            {
                await socket.ReceiveAsync(receiveBuffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                return true;
            }

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task BulkLoopAsync(
        ArmContext context,
        MixCounters counters,
        int desktopIndex,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        const int frameLength = FrameCodec.HeaderSize + BulkPayloadBytes + FrameCodec.TrailerSize;
        using var socket = context.CreateTcpSocket();
        if (!await SocketOps.TryConnectAsync(socket, context.TcpEndPoint, cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref counters._bulkErrors);
            return;
        }

        var frame = new FrameBuffer(BulkPayloadBytes);
        var reader = new FrameStreamReader(socket, 128 * 1024);
        var limiter = new AggregateRateLimiter(BulkBitsPerSecond / 8, long.MaxValue, startTicks);
        var connectionId = 0x4D42_0000u + (uint)desktopIndex;
        ulong sequence = 0;

        try
        {
            await SocketOps.SendCommandAsync(socket, connectionId, TcpMode.Clean, 0, cancellationToken).ConfigureAwait(false);
            while (Clock.Now < deadlineTicks)
            {
                if (!limiter.TryReserve(frameLength, out var waitUntilTicks))
                {
                    break;
                }

                if (waitUntilTicks > 0)
                {
                    await Pacer.WaitUntilAsync(waitUntilTicks, cancellationToken).ConfigureAwait(false);
                }

                var length = frame.Build(connectionId, ++sequence, Clock.Now);
                await socket.SendAsync(frame.Memory[..length], SocketFlags.None, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref counters._bulkBytesSent, length);

                var status = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (status != FrameReadStatus.Frame)
                {
                    Interlocked.Increment(ref counters._bulkErrors);
                    break;
                }

                Interlocked.Add(ref counters._bulkBytes, reader.Raw.Length);
                Interlocked.Increment(ref counters.BulkFramesPerDesktop[desktopIndex]);
            }

            SocketOps.ShutdownQuietly(socket, SocketShutdown.Send);
        }
        catch (SocketException)
        {
            Interlocked.Increment(ref counters._bulkErrors);
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (ObjectDisposedException)
        {
            Interlocked.Increment(ref counters._bulkErrors);
        }
    }

    private static async Task UdpLoopAsync(
        ArmContext context,
        MixCounters counters,
        UdpReliabilityTracker tracker,
        long[] observationEnds,
        int desktopIndex,
        long startTicks,
        long deadlineTicks,
        long windowTicks,
        CancellationToken cancellationToken)
    {
        using var socket = context.CreateUdpSocket();
        await socket.ConnectAsync(context.UdpEndPoint, cancellationToken).ConfigureAwait(false);
        var frame = new FrameBuffer(UdpPayloadBytes);
        var receiveBuffer = new byte[FrameCodec.HeaderSize + UdpPayloadBytes + FrameCodec.TrailerSize + 64];
        var pending = new ConcurrentDictionary<ulong, long>();
        var connectionId = 0x4D55_0000u + (uint)desktopIndex;
        using var laneCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = ReceiveUdpLoopAsync(context, socket, connectionId, pending, tracker, receiveBuffer, laneCancellation.Token);
        var pacer = new Pacer(UdpPacketsPerSecond, startTicks);
        // MaxValue until the send loop sets the horizon: a lane that dies before sending keeps
        // observing until the instant it dies, so nothing is called lost without its whole W.
        var drainUntilTicks = long.MaxValue;
        long index = 0;

        try
        {
            while (Clock.Now < deadlineTicks)
            {
                var intended = pacer.IntendedTicks(index);
#pragma warning disable S6966, VSTHRD103, MA0042 // Sub-millisecond open-loop pacing; Task.Delay cannot hold these instants on Windows.
                // ReSharper disable once MethodHasAsyncOverload // The pacing must block: WaitUntilAsync's Task.Delay resolves to the 15.6 ms Windows timer tick and cannot hold these instants.
                Pacer.WaitUntil(intended, laneCancellation.Token);
#pragma warning restore S6966, VSTHRD103, MA0042
                index++;
                tracker.MarkSupplied();
                // Registered before the socket call so a concurrent receive lane can never see a reply
                // for a sequence the socket has not sent; a refused send is un-booked in the catch.
                tracker.MarkSent(index, intended);
                pending[(ulong)index] = intended;
                var length = frame.Build(connectionId, (ulong)index, intended);
                await socket.SendAsync(frame.Memory[..length], SocketFlags.None, laneCancellation.Token).ConfigureAwait(false);
                drainUntilTicks = Clock.Now + windowTicks;
                Interlocked.Add(ref counters._udpBytes, length);
            }

            await DrainPendingAsync(pending, drainUntilTicks, laneCancellation.Token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            tracker.MarkSendRefused(index);
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }
        finally
        {
            // The instant observation stopped, capped by the horizon: Classify books a datagram still
            // inside its window at this instant as abandoned, so one datagram is never both client
            // loss and path loss.
            observationEnds[desktopIndex] = Math.Min(Clock.Now, drainUntilTicks);
        }

        await laneCancellation.CancelAsync().ConfigureAwait(false);
        await receive.ConfigureAwait(false);
    }

    private static void BookUndecodable(UdpReliabilityTracker tracker, ReadOnlySpan<byte> datagram, FrameDecodeError error)
    {
        if (error == FrameDecodeError.BadChecksum && FrameCodec.TryReadHeader(datagram, out var partial, out _))
        {
            tracker.MarkCorruptWithKnownSequence((long)partial.Sequence);
            return;
        }

        tracker.MarkCorrupt();
    }

    private static async Task DrainPendingAsync(ConcurrentDictionary<ulong, long> pending, long drainUntilTicks, CancellationToken cancellationToken)
    {
        while (!pending.IsEmpty && Clock.Now < drainUntilTicks)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveUdpLoopAsync(
        ArmContext context,
        Socket socket,
        uint connectionId,
        ConcurrentDictionary<ulong, long> pending,
        UdpReliabilityTracker tracker,
        byte[] receiveBuffer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(receiveBuffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                var now = Clock.Now;
                var datagram = receiveBuffer.AsSpan(0, received);

                if (!FrameCodec.TryDecode(datagram, out var header, out var payload, out var error))
                {
                    BookUndecodable(tracker, datagram, error);
                    continue;
                }

                if (header.ConnectionId != connectionId)
                {
                    tracker.MarkForeignConnection();
                    continue;
                }

                if (!Filler.Matches(header.ConnectionId, header.Sequence, payload))
                {
                    tracker.MarkCorruptWithKnownSequence((long)header.Sequence);
                    continue;
                }

                if (!tracker.WasSent((long)header.Sequence))
                {
                    tracker.MarkUnmatchedReply();
                    continue;
                }

                if (pending.TryRemove(header.Sequence, out var intended))
                {
                    context.Latency.UdpRtt.Record(Clock.ToNanoseconds(now - intended));
                }

                tracker.MarkArrival((long)header.Sequence, now, payload.Length);
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
        catch (SocketException)
        {
            tracker.MarkSendFailure();
        }
        catch (ObjectDisposedException)
        {
            /* teardown closed the socket first */
        }
    }
}
