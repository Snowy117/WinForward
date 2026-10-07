using System.Net.Sockets;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Contracts.Metrics;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Arms;

internal static class LatencyArm
{
    // How much room a lane's receive buffer keeps past one whole frame, so a datagram that is not a
    // frame can still be read and judged instead of being cut off at the buffer's edge.
    private const int ReceiveSlack = 64;

    private const uint UdpConnectionId = 0x7100_0001u;
    private const uint TcpConnectionIdBase = 0x7400_0000u;

    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var plan = new LatencyPlan(context.Spec);

        var startTicks = Clock.Now;
        var deadlineTicks = context.DeadlineTicks(startTicks);
        using var linked = context.CreateLinkedTokenSource();
        var lanes = new LaneStates(plan);
        var tasks = StartLanes(context, plan, lanes, startTicks, deadlineTicks, linked.Token);

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var elapsedTicks = Clock.Now - startTicks;

        // Both totals are formed here, once every lane has joined: the engine's send-side snapshot and
        // the policy's own book are joined by lane, never by a counter that one of them copied.
        var tcp = TotalTcp(lanes.Tcp, lanes.Probe);
        var udp = TotalUdp(lanes.Udp);

        var outcome = new ArmOutcome
        {
            Parameters = plan.Parameters(),

            // The metrics value is built after the lanes joined, so a protocol the arm did not run
            // publishes no block at all instead of a block of zeros that would read as a measurement.
            Metrics = new LatencyMetrics
            {
                Tcp = plan.UseTcp ? LatencyMetricsWriter.TcpMetrics(tcp, lanes.Tcp, plan, elapsedTicks) : null,
                Udp = plan.UseUdp ? LatencyMetricsWriter.UdpMetrics(udp, plan, elapsedTicks) : null,
            },
        };

        var validity = new MeasurementValidity(plan, lanes, tcp, udp, elapsedTicks);
        LatencyMetricsWriter.WriteGates(outcome, tcp, udp, validity);
        LatencyMetricsWriter.WriteNotes(outcome, plan, tcp, udp, validity, elapsedTicks);
        return outcome;
    }

    private static List<Task> StartLanes(
        ArmContext context,
        LatencyPlan plan,
        LaneStates lanes,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        if (plan.UseTcp)
        {
            // Own thread per lane: a lane whose first awaits complete synchronously would otherwise
            // run to completion here and the lanes after it would never be constructed.
            for (var lane = 0; lane < plan.Lanes; lane++)
            {
                var laneState = lanes.Tcp[lane];
                var laneIndex = lane;
                tasks.Add(Dedicated.RunOnOwnThreadAsync(() => RunTcpLaneAsync(context, laneState, laneIndex, plan, startTicks, deadlineTicks, cancellationToken)));
            }

            tasks.Add(Dedicated.RunOnOwnThreadAsync(() => RunConnectProbeAsync(context, lanes.Probe, startTicks, deadlineTicks, cancellationToken)));
        }

        if (plan.UseUdp)
        {
            tasks.Add(Dedicated.RunOnOwnThreadAsync(() => RunUdpLaneAsync(context, lanes.Udp, plan, startTicks, deadlineTicks, cancellationToken)));
        }

        return tasks;
    }

    /// <summary>
    /// What one lane tells the engine about itself: the pace and deadline it shares with every other
    /// lane, its own share of the deferred backlog, and the two buffers the engine reuses for every
    /// slot. The receive buffer holds one whole frame plus the slack a non-frame datagram is read into.
    /// </summary>
    private static LaneEngineOptions LaneOptions(LatencyPlan plan, long startTicks, long deadlineTicks) => new()
    {
        RatePerSecond = plan.Rate,
        StartTicks = startTicks,
        DeadlineTicks = deadlineTicks,
        BacklogLimit = plan.BacklogLimit,
        SendBufferBytes = FrameCodec.HeaderSize + plan.PayloadBytes + FrameCodec.TrailerSize,
        ReceiveBufferBytes = FrameCodec.HeaderSize + plan.PayloadBytes + FrameCodec.TrailerSize + ReceiveSlack,

        // The arm's own bound, passed rather than left to the engine's default, so the record's note
        // and the bound the drain actually ran with cannot drift apart.
        DrainLimitTicks = Clock.FromSeconds(LatencyPlan.DrainSeconds),
    };

    private static LatencyTotals TotalTcp(List<Lane<LatencyTcpState>> lanes, LatencyTcpState probe)
    {
        var total = new LatencyTotals();
        foreach (var lane in lanes)
        {
            total.AddTcp(lane);
            total.AddPending(lane.Book.Pending);
        }

        total.AddProbe(probe);
        return total;
    }

    private static LatencyTotals TotalUdp(Lane<UdpLatencyState> lane)
    {
        var total = new LatencyTotals();
        total.AddUdp(lane);
        total.AddPending(lane.Book.Pending);
        return total;
    }

    private static async Task RunTcpLaneAsync(
        ArmContext context,
        Lane<LatencyTcpState> lane,
        int laneIndex,
        LatencyPlan plan,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        lane.Book.Started = 1;
        var connectionId = TcpConnectionIdBase + (uint)laneIndex;
        var options = LaneOptions(plan, startTicks, deadlineTicks);

        // The transport owns the socket: it connects, sends the lane's command frame and reads the
        // stream, and the engine disposes nothing.
        using var transport = new TcpLaneTransport(context.CreateTcpSocket(), context.TcpEndPoint, connectionId);
        var policy = new LatencyTcpPolicy(lane.Book, connectionId, plan.PayloadBytes, plan.InFlightWindow, context.Latency.TcpRtt);
        var engine = new LaneEngine<TcpLaneTransport>(transport, policy, options);
        lane.Counts = await engine.RunAsync(cancellationToken).ConfigureAwait(false);
        lane.Book.BookConnect(transport.ConnectOk, transport.ConnectTicks);
    }

    private static async Task RunConnectProbeAsync(
        ArmContext context,
        LatencyTcpState state,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        var intervalTicks = Clock.FromSeconds(1);
        long index = 0;
        try
        {
            while (true)
            {
                var intended = startTicks + (index * intervalTicks);
                if (intended >= deadlineTicks)
                {
                    return;
                }

                await Pacer.WaitUntilAsync(intended, cancellationToken).ConfigureAwait(false);
                using var socket = context.CreateTcpSocket();
                var begin = Clock.Now;
                var open = await SocketOps.TryConnectAsync(socket, context.TcpEndPoint, cancellationToken).ConfigureAwait(false);
                var end = Clock.Now;
                state.BookConnect(open.Ok, end - begin);
                if (!open.Ok)
                {
                    index++;
                    continue;
                }

                context.Latency.TcpConnect.Record(Clock.ToNanoseconds(end - intended));
                SocketOps.ShutdownQuietly(socket, SocketShutdown.Both);
                index++;
            }
        }
        catch (OperationCanceledException)
        {
            /* the arm deadline or a shutdown request ended the loop */
        }
    }

    private static async Task RunUdpLaneAsync(
        ArmContext context,
        Lane<UdpLatencyState> lane,
        LatencyPlan plan,
        long startTicks,
        long deadlineTicks,
        CancellationToken cancellationToken)
    {
        lane.Book.Started = 1;
        var options = LaneOptions(plan, startTicks, deadlineTicks);
        using var transport = new UdpLaneTransport(context.CreateUdpSocket(), context.UdpEndPoint, options.ReceiveBufferBytes);
        var policy = new UdpLatencyPolicy(lane.Book, UdpConnectionId, plan.PayloadBytes, plan.InFlightWindow, context.Latency.UdpRtt);
        var engine = new LaneEngine<UdpLaneTransport>(transport, policy, options);
        lane.Counts = await engine.RunAsync(cancellationToken).ConfigureAwait(false);

        // A udp lane books no connect facts: the record publishes them under tcp.* only (a udp connect
        // has no handshake to time), so a failed open shows up the way a tcp lane's does -- through
        // scheduleTruncated and laneShortfall -- instead of inflating the tcp connect population.
    }
}

/// <summary>
/// One lane's two halves, kept apart until the totals are formed: the engine's send-side snapshot
/// and the policy's own book. Neither half is reachable from the other, which is what the
/// disjointness contract (D18.1) asks for.
/// </summary>
internal sealed class Lane<TBook>
    where TBook : new()
{
    internal TBook Book { get; } = new();

    internal LaneCounts Counts { get; set; }
}

/// <summary>
/// Per-lane containers: each lane writes only its own state and the totals are formed after the
/// lanes join.
/// </summary>
internal sealed class LaneStates
{
    internal LaneStates(LatencyPlan plan)
    {
        // States exist before any lane starts, so a lane that never runs still appears in the
        // per-lane arrays instead of shrinking them.
        for (var lane = 0; plan.UseTcp && lane < plan.Lanes; lane++)
        {
            Tcp.Add(new Lane<LatencyTcpState>());
        }
    }

    internal List<Lane<LatencyTcpState>> Tcp { get; } = [];

    internal LatencyTcpState Probe { get; } = new();

    internal Lane<UdpLatencyState> Udp { get; } = new();
}
