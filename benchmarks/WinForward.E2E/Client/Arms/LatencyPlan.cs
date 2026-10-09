using System.Runtime.InteropServices;
using WinForward.E2E.Client.Lanes;
using WinForward.E2E.Contracts;

namespace WinForward.E2E.Client.Arms;

/// <summary>The effective run shape after plan defaults, shared by every lane and by the record.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct LatencyPlan
{
    private const int DefaultRatePerSecond = 20;
    private const int DefaultPayloadBytes = 120;
    private const int DefaultWindow = 64;
    private const int DefaultLanes = 1;

    // How long a lane may keep deferring work, in seconds of its own offered rate: long enough for
    // any stall the arm should survive, bounded so a product that never answers cannot allocate
    // without limit.
    internal const int BacklogSeconds = 10;

    // Ceiling for plans that offer absurd rates, so the first act is never a huge allocation.
    internal const int MaxBacklogPerLane = 1 << 20;

    // The receive side drains after the offer loop so the last in-flight cohort is sampled rather
    // than cancelled; the bound keeps a silent product from holding the arm open.
    internal const int DrainSeconds = 1;

    internal LatencyPlan(ArmSpec spec)
    {
        Seconds = spec.Seconds;
        Protocol = spec.Protocol;
        Rate = spec.RatePerSecond > 0 ? spec.RatePerSecond : DefaultRatePerSecond;
        PayloadBytes = spec.PayloadBytes > 0 ? spec.PayloadBytes : DefaultPayloadBytes;
        InFlightWindow = spec.Window > 0 ? spec.Window : DefaultWindow;
        Lanes = spec.Lanes > 0 ? spec.Lanes : DefaultLanes;
        BacklogLimit = BacklogCapacity(Rate, InFlightWindow);
        UseTcp = spec.Protocol is "tcp" or "tcp+udp";
        UseUdp = spec.Protocol is "udp" or "tcp+udp";
    }

    private double Seconds { get; }

    private string Protocol { get; }

    internal int Rate { get; }

    internal int PayloadBytes { get; }

    internal int InFlightWindow { get; }

    internal int Lanes { get; }

    internal int BacklogLimit { get; }

    internal bool UseTcp { get; }

    internal bool UseUdp { get; }

    /// <summary>
    /// The load this arm actually ran with, after its own defaults were applied.
    /// </summary>
    internal ArmParameters Parameters() => new()
    {
        Seconds = Seconds,
        RatePerSecond = Rate,
        PayloadBytes = PayloadBytes,
        Protocol = Protocol,
        Lanes = Lanes,

        // in-flight requests per lane; the loss arm's window is a millisecond threshold, so the
        // two arms must not publish one bare "window" that means different things.
        InFlightWindow = InFlightWindow,
    };

    private static int BacklogCapacity(int rate, int window) =>
        (int)Math.Min(MaxBacklogPerLane, Math.Max((long)window * 4, (long)rate * BacklogSeconds));
}

/// <summary>
/// The arm's published counters as one value: the engine's send-side snapshot and the policy's own
/// book, joined per lane and then summed. It is the sum and nothing else — the engine owns what was
/// offered and sent, the policy owns what came back — so a published number can never drift from
/// the counter that produced it.
/// </summary>
internal sealed class LatencyTotals
{
    internal long Started { get; private set; }

    internal long Supplied { get; private set; }

    internal long SentOk { get; private set; }

    internal long SendWouldBlock { get; private set; }

    internal long WindowOverflow { get; private set; }

    internal long BacklogDrops { get; private set; }

    internal long SendFailures { get; private set; }

    /// <summary>Unanswered requests plus deferred intents that never got a slot, when the lane stopped.</summary>
    internal long Outstanding { get; private set; }

    internal long Received { get; private set; }

    internal long Corrupt { get; private set; }

    internal long UnmatchedReplies { get; private set; }

    internal long ForeignConnection { get; private set; }

    internal long RemoteClosed { get; private set; }

    internal long ProtocolErrors { get; private set; }

    internal long ConnectSamples { get; private set; }

    internal long ConnectFailures { get; private set; }

    internal long ConnectTicks { get; private set; }

    internal bool Truncated { get; private set; }

    /// <summary>
    /// Requests that reached no latency histogram: supplied minus sent, which only counts samples
    /// the client itself destroyed.
    /// </summary>
    internal long ClientSendLoss => Math.Max(0, Supplied - SentOk);

    internal void AddTcp(Lane<LatencyTcpState> lane)
    {
        AddCounts(lane.Counts);
        var book = lane.Book;
        Started += book.Started;
        Received += book.Received;
        Corrupt += book.Corrupt;
        ProtocolErrors += book.ProtocolErrors;
        RemoteClosed += book.RemoteClosed;
        UnmatchedReplies += book.UnmatchedReplies;
        AddConnect(book);
    }

    internal void AddUdp(Lane<UdpLatencyState> lane)
    {
        AddCounts(lane.Counts);
        var book = lane.Book;
        Started += book.Started;
        Received += book.Received;
        Corrupt += book.Corrupt;
        ProtocolErrors += book.ProtocolErrors;
        ForeignConnection += book.ForeignConnection;
        UnmatchedReplies += book.UnmatchedReplies;
    }

    /// <summary>Adds the policy half of <c>outstandingAtTeardown</c>: requests no reply consumed.</summary>
    internal void AddPending(long pending) => Outstanding += pending;

    /// <summary>The connect probe is not a lane: it has connect facts and no engine counts at all.</summary>
    internal void AddProbe(LatencyTcpState probe)
    {
        ConnectSamples += probe.ConnectSamples;
        ConnectFailures += probe.ConnectFailures;
        ConnectTicks += probe.ConnectTicks;
    }

    private void AddCounts(LaneCounts counts)
    {
        Supplied += counts.Supplied;
        SentOk += counts.SentOk;
        SendWouldBlock += counts.SendWouldBlock;
        SendFailures += counts.SendFailures;

        // The engine's two defer counters are the contract's two defer keys, by name and by
        // value; the arm does not keep a second pair.
        WindowOverflow += counts.DeferredQueued;
        BacklogDrops += counts.DeferredDropped;

        // The other half of outstandingAtTeardown: intents the window never let out.
        Outstanding += counts.DeferredPending;
        Truncated |= counts.ScheduleTruncated;
    }

    private void AddConnect(LatencyTcpState book)
    {
        ConnectSamples += book.ConnectSamples;
        ConnectFailures += book.ConnectFailures;
        ConnectTicks += book.ConnectTicks;
    }
}

/// <summary>
/// Whether the record measured what it offered: lanes run, schedule shortfall, and the ceiling
/// on directly measurable latency. Derived once so gates and notes cannot disagree.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly struct MeasurementValidity
{
    internal MeasurementValidity(LatencyPlan plan, LaneStates lanes, LatencyTotals tcp, LatencyTotals udp, long elapsedTicks)
    {
        PlannedLanes = (plan.UseTcp ? plan.Lanes : 0) + (plan.UseUdp ? 1 : 0);
        StartedLanes = (plan.UseTcp ? tcp.Started : 0) + (plan.UseUdp ? udp.Started : 0);
        var idleLanes = (plan.UseTcp ? CountIdleLanes(lanes.Tcp) : 0) + (IsIdle(udp.Started, udp.Supplied) ? 1 : 0);
        LaneShortfall = PlannedLanes - StartedLanes + idleLanes;
        TcpCeilingMs = plan.UseTcp ? LatencyMetricsWriter.InFlightCeilingMs(plan.InFlightWindow, plan.Lanes, tcp.SentOk, elapsedTicks) : 0;
        UdpCeilingMs = plan.UseUdp ? LatencyMetricsWriter.InFlightCeilingMs(plan.InFlightWindow, 1, udp.SentOk, elapsedTicks) : 0;
        Truncated = tcp.Truncated || udp.Truncated || LaneShortfall > 0;
    }

    internal long PlannedLanes { get; }

    internal long StartedLanes { get; }

    internal long LaneShortfall { get; }

    internal double TcpCeilingMs { get; }

    internal double UdpCeilingMs { get; }

    internal bool Truncated { get; }

    private static bool IsIdle(long started, long supplied) => started > 0 && supplied == 0;

    private static long CountIdleLanes(List<Lane<LatencyTcpState>> lanes)
    {
        long idle = 0;
        foreach (var lane in lanes)
        {
            idle += IsIdle(lane.Book.Started, lane.Counts.Supplied) ? 1 : 0;
        }

        return idle;
    }
}
