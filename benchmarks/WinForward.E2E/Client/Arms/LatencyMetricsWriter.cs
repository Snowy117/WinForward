using System.Diagnostics;
using System.Globalization;
using WinForward.E2E.Contracts;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Client.Arms;

/// <summary>
/// Turns one latency arm's totals into the record's measurements, gates and notes: the two protocol
/// metric blocks, the ten gates and the prose that explains the run. Everything here reads the totals
/// the arm formed after the lanes joined, so no writer can publish a counter the arm did not sum.
/// </summary>
internal static class LatencyMetricsWriter
{
    /// <summary>
    /// The ceiling on directly measurable latency: a request slower than window / (per-lane achieved
    /// rate) no longer frees its slot before the next offer, so everything past it is measured
    /// through the deferred queue instead.
    /// </summary>
    internal static double InFlightCeilingMs(int window, int lanes, long sentOk, long elapsedTicks)
    {
        var achievedPerSecond = JsonPerSecond.PerSecond(sentOk, elapsedTicks, Stopwatch.Frequency);
        if (achievedPerSecond is not > 0)
        {
            // No elapsed time, or nothing sent: the ceiling is undefined, and it stays the 0 the
            // pre-change arithmetic published rather than taking the record's null convention,
            // because this value is a floor on a measurable latency and not a rate.
            return 0;
        }

        return NumberFormat.Round(1000.0 * window * lanes / achievedPerSecond.GetValueOrDefault());
    }

    internal static void WriteGates(ArmOutcome outcome, LatencyTotals tcp, LatencyTotals udp, MeasurementValidity validity)
    {
        outcome.Gates[ArmKeys.Common.Gates.ClientSendLoss] = tcp.ClientSendLoss + udp.ClientSendLoss;
        outcome.Gates[ArmKeys.Common.Gates.WindowOverflow] = tcp.WindowOverflow + udp.WindowOverflow;
        outcome.Gates[ArmKeys.Common.Gates.BacklogDrops] = tcp.BacklogDrops + udp.BacklogDrops;
        outcome.Gates[ArmKeys.Common.Gates.SendFailures] = tcp.SendFailures + udp.SendFailures;
        outcome.Gates[ArmKeys.Common.Gates.LanesPlanned] = validity.PlannedLanes;
        outcome.Gates[ArmKeys.Common.Gates.LanesStarted] = validity.StartedLanes;
        outcome.Gates[ArmKeys.Common.Gates.LaneShortfall] = validity.LaneShortfall;
        outcome.Gates[ArmKeys.Common.Gates.ScheduleTruncated] = validity.Truncated ? 1L : 0L;
        outcome.Gates[ArmKeys.Common.Gates.InFlightCeilingMs] = TightestCeiling(validity.TcpCeilingMs, validity.UdpCeilingMs);
        outcome.Gates[ArmKeys.Common.Gates.WindowMs] = 0L;
    }

    internal static void WriteNotes(
        ArmOutcome outcome,
        LatencyPlan plan,
        LatencyTotals tcp,
        LatencyTotals udp,
        MeasurementValidity validity,
        long elapsedTicks)
    {
        outcome.Notes.Add("latency is measured from each request's intended send instant, never from the actual send instant.");
        outcome.Notes.Add("sendWouldBlock counts sends the kernel did not accept synchronously; those sends still complete, so they are reported but not folded into clientSendLoss.");
        outcome.Notes.Add("a reply carrying another flow's connection id validates against that flow's own filler, so it is rejected by connection id before the filler check and booked as foreignConnection; only a reply that carries this socket's own id and fails its filler is booked as corrupt.");
        outcome.Notes.Add(string.Concat(
            "tcp.connectAttempts counts every TCP connect the arm made -- the per-lane lane connects plus the 1 Hz connect probe -- and tcp.connectFailures counts the attempts in that same population that failed, so tcp.meanConnectMs is the mean connect duration over its successful attempts and is null when there were none; ",
            "latency.tcp-connect separately holds the probe's own samples, measured from each probe's intended instant."));
        outcome.Notes.Add("a request offered at a full in-flight window is deferred, not dropped: it goes out as soon as a reply frees a slot, still stamped with its original intended instant, so a stalled product is published as an inflated sample instead of a missing one; windowOverflow counts those deferrals.");
        outcome.Notes.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"the deferred queue holds at most {plan.BacklogLimit} request(s) per lane, i.e. {LatencyPlan.BacklogSeconds} s of the offered rate capped at {LatencyPlan.MaxBacklogPerLane}; a request that bound discards is counted in backlogDrops and folded into gates.clientSendLoss, so an arm that censors its own tail cannot publish a clean gate."));
        outcome.Notes.Add("sendFailures counts individual sends that threw; the offer loop continues after one, so a transient error costs that request's sample instead of the rest of the schedule, and that sample is folded into gates.clientSendLoss.");
        outcome.Notes.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"each lane drains its receive side for up to {LatencyPlan.DrainSeconds} s after the offer loop so the cohort still in flight is sampled rather than discarded; outstandingAtTeardown counts what was still unanswered, plus any deferred request that never got a slot, when the bound expired."));
        outcome.Notes.Add("tcp and udp counters are summed only after every lane has joined, so sentOk cannot exceed supplied however many lanes a plan asks for.");

        if (plan.UseTcp)
        {
            outcome.Notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"in-flight ceiling (tcp): {plan.Lanes} lane(s) at {RateText(JsonPerSecond.PerSecond(tcp.SentOk, elapsedTicks, Stopwatch.Frequency))} requests/s aggregate achieved with inFlightWindow {plan.InFlightWindow} per lane put the measurable direct-latency ceiling at {validity.TcpCeilingMs:F1} ms; slower requests are measured through the deferred queue, and the ceiling was {(tcp.WindowOverflow > 0 ? "reached" : "not reached")} (windowOverflow = {tcp.WindowOverflow})."));
        }

        if (plan.UseUdp)
        {
            outcome.Notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"in-flight ceiling (udp): 1 lane at {RateText(JsonPerSecond.PerSecond(udp.SentOk, elapsedTicks, Stopwatch.Frequency))} requests/s achieved with inFlightWindow {plan.InFlightWindow} put the measurable direct-latency ceiling at {validity.UdpCeilingMs:F1} ms; slower requests are measured through the deferred queue, and the ceiling was {(udp.WindowOverflow > 0 ? "reached" : "not reached")} (windowOverflow = {udp.WindowOverflow})."));
        }

        outcome.Notes.Add(string.Concat(
            "lanes actually run: ",
            plan.UseTcp ? string.Create(CultureInfo.InvariantCulture, $"tcp {tcp.Started} of {plan.Lanes}") : string.Empty,
            // ReSharper disable once MergeIntoPattern // Two independent plan flags read better as a conjunction than as a property pattern.
            plan.UseTcp && plan.UseUdp ? ", " : string.Empty,
            plan.UseUdp ? string.Create(CultureInfo.InvariantCulture, $"udp {udp.Started} of 1") : string.Empty,
            "; per-lane supplied and sent counts are published as tcp.laneSupplied and tcp.laneSentOk."));

        if (tcp.Truncated || udp.Truncated)
        {
            outcome.Notes.Add("a lane never connected or an offer loop ended before its deadline, so part of the offered schedule was never offered at all; gates.scheduleTruncated marks the record.");
        }

        if (validity.LaneShortfall > 0)
        {
            outcome.Notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{validity.StartedLanes} of {validity.PlannedLanes} planned lane(s) ran and supplied traffic; a lane that supplies nothing is a harness failure because it removes its whole share of the offered schedule (gates.laneShortfall)."));
        }
    }

    internal static LatencyTcpMetrics TcpMetrics(LatencyTotals state, List<Lane<LatencyTcpState>> laneStates, LatencyPlan plan, long elapsedTicks)
    {
        var laneSupplied = new long[laneStates.Count];
        var laneSentOk = new long[laneStates.Count];
        for (var lane = 0; lane < laneStates.Count; lane++)
        {
            laneSupplied[lane] = laneStates[lane].Counts.Supplied;
            laneSentOk[lane] = laneStates[lane].Counts.SentOk;
        }

        return new LatencyTcpMetrics
        {
            LaneStarted = state.Started,
            LaneSupplied = laneSupplied,
            LaneSentOk = laneSentOk,
            Supplied = state.Supplied,
            Sent = state.SentOk,
            SendWouldBlock = state.SendWouldBlock,
            WindowOverflow = state.WindowOverflow,
            BacklogDrops = state.BacklogDrops,
            SendFailures = state.SendFailures,
            AbandonedAtTeardown = Math.Max(0, state.Supplied - state.SentOk - state.SendFailures - state.BacklogDrops),
            ClientSendLoss = state.ClientSendLoss,
            Received = state.Received,
            OutstandingAtTeardown = state.Outstanding,
            Corrupt = state.Corrupt,
            ProtocolErrors = state.ProtocolErrors,
            RemoteClosed = state.RemoteClosed,
            UnmatchedReplies = state.UnmatchedReplies,
            WindowCeilingMs = InFlightCeilingMs(plan.InFlightWindow, plan.Lanes, state.SentOk, elapsedTicks),
            ScheduleTruncated = state.Truncated ? 1L : 0L,
            AchievedRate = JsonPerSecond.PerSecond(state.SentOk, elapsedTicks, Stopwatch.Frequency),
            ConnectAttempts = state.ConnectSamples + state.ConnectFailures,
            ConnectFailures = state.ConnectFailures,
            MeanConnectMs = state.ConnectSamples == 0
                ? null
                : NumberFormat.Round(Clock.ToMicroseconds(state.ConnectTicks) / (double)state.ConnectSamples / 1000.0),
        };
    }

    internal static LatencyUdpMetrics UdpMetrics(LatencyTotals state, LatencyPlan plan, long elapsedTicks) => new()
    {
        LaneStarted = state.Started,
        Supplied = state.Supplied,
        Sent = state.SentOk,
        SendWouldBlock = state.SendWouldBlock,
        WindowOverflow = state.WindowOverflow,
        BacklogDrops = state.BacklogDrops,
        SendFailures = state.SendFailures,
        AbandonedAtTeardown = Math.Max(0, state.Supplied - state.SentOk - state.SendFailures - state.BacklogDrops),
        ClientSendLoss = state.ClientSendLoss,
        Received = state.Received,
        Corrupt = state.Corrupt,
        ProtocolErrors = state.ProtocolErrors,
        UnmatchedReplies = state.UnmatchedReplies,
        ForeignConnection = state.ForeignConnection,
        OutstandingAtTeardown = state.Outstanding,
        WindowCeilingMs = InFlightCeilingMs(plan.InFlightWindow, 1, state.SentOk, elapsedTicks),
        ScheduleTruncated = state.Truncated ? 1L : 0L,

        // Sent minus matched arrivals, never sent minus received, which duplicates can exceed.
        LossRate = JsonRate.Rate(state.SentOk - (state.Received - state.UnmatchedReplies), state.SentOk),
        AchievedRate = JsonPerSecond.PerSecond(state.SentOk, elapsedTicks, Stopwatch.Frequency),
    };

    /// <summary>
    /// Renders a rate for a note. A note is prose, so an undefined rate says so instead of leaving
    /// the hole an interpolated null leaves between two spaces.
    /// </summary>
    private static string RateText(double? perSecond) =>
        perSecond is { } value ? value.ToString("F3", CultureInfo.InvariantCulture) : "n/a";

    private static double TightestCeiling(double first, double second)
    {
        if (first <= 0)
        {
            return second;
        }

        return second <= 0 ? first : Math.Min(first, second);
    }
}
