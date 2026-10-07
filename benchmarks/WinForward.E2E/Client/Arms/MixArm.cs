using WinForward.E2E.Contracts;

namespace WinForward.E2E.Client.Arms;

internal static class MixArm
{
    private const int DefaultDesktops = 4;
    // Published per desktop as udpPacketsPerSecondPerDesktop; mix's UDP rate is not a plan key, so
    // this is also the rate the plan loader checks the offered schedule against.
    internal const int UdpPacketsPerSecond = 30;

    internal static async Task<ArmOutcome> RunAsync(ArmContext context)
    {
        var desktops = context.Spec.Desktops > 0 ? context.Spec.Desktops : DefaultDesktops;
        var windowMs = context.Spec.LossWindowMs > 0 ? context.Spec.LossWindowMs : UdpLossMath.DefaultWindowMilliseconds;
        var windowTicks = UdpLossMath.WindowTicks(windowMs);
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

        var (metrics, clientSendLoss, idleLanes) = MixMetricsWriter.WriteMetrics(counters, trackers, observationEnds, windowMs, windowTicks, Clock.Now - startTicks);
        var outcome = new ArmOutcome
        {
            Parameters = new ArmParameters
            {
                Seconds = context.Spec.Seconds,
                Desktops = desktops,
                PageIntervalSeconds = MixPageLoop.s_pageInterval.TotalSeconds,
                PageConnections = MixPageLoop.PageConnections,
                PageRequestsTotal = MixPageLoop.PageTotalRequests,
                PageMessageBytes = MixPageLoop.PageMessageBytes,
                BulkBitsPerSecondPerDesktop = MixBulkLoop.BulkBitsPerSecond,
                DnsQueriesPerPage = MixPageLoop.DnsQueriesPerPage,
                UdpPacketsPerSecondPerDesktop = UdpPacketsPerSecond,
                UdpPayloadBytes = MixUdpLoop.UdpPayloadBytes,
                LossWindowMs = windowMs,
            },
            Metrics = metrics,
            Gates =
            {
                [ArmKeys.Common.Gates.ClientSendLoss] = clientSendLoss,
                [ArmKeys.Common.Gates.IdleLanes] = idleLanes,
                [ArmKeys.Common.Gates.WindowMs] = windowMs,
            },
        };
        outcome.Notes.Add("W is the plan's lossWindowMs, 200 ms when the plan does not declare one: it is declared and published as classes.udp.window rather than derived, so every row's arrived/late/never split is reproducible from the record alone (RFC 2680 style).");
        outcome.Notes.Add("every desktop's UDP drain runs until W after that desktop's last real send, so no datagram is called lost before its whole W has passed; datagrams still inside their window when a drain is cut short are counted in classes.udp.abandonedAtTeardown and in clientSendLoss, never in never, and they stay out of lossRate.");
        outcome.Notes.Add("every sent datagram is classified exactly once, so arrived + late + never + abandonedAtTeardown + corruptDatagrams == sent by construction, exactly as in LOSS, and classes.udp.clientSendLoss is classes.udp.abandonedAtTeardown + classes.udp.sendFailures + classes.udp.windowOverflow with all three terms published beside it; corruptDatagrams counts the sent datagrams booked corrupt while corrupt counts corrupt arrivals, windowOverflow is structurally zero because the MIX UDP class has no in-flight window, and sendFailures counts datagrams the socket refused, which is why a refused datagram is client loss and never path loss.");
        outcome.Notes.Add("sentPerDesktop and desktops carry one witness per flow class per desktop, and gates.idleLanes counts the witnesses that stayed at zero: a lane that never ran is a harness failure, not a smaller aggregate.");
        outcome.Notes.Add("reordered counts arrivals that follow the arrival of a higher sequence, the RFC 4737 definition; foreignConnection counts replies carrying another flow's connection id, so a non-zero value means the product mixed datagrams between flows. classes.udp.outOfRangeSequences counts sequences the tracker refused as outside its bounded sequence space: a non-zero value means part of the offered schedule was never tracked, so the classification covers fewer datagrams than sent.");
        outcome.Notes.Add("every flow class carries the shared frame layout, so the per-class byte and latency numbers come from the same measurement path as the dedicated arms.");
        outcome.Notes.Add("a rate whose denominator is zero is written as null rather than 0: nothing was sent, so there is no rate to report.");
        return outcome;
    }

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
            Dedicated.RunOnOwnThreadAsync(() => MixPageLoop.PageLoopAsync(context, counters, desktopIndex, startTicks, deadlineTicks, token)),
            Dedicated.RunOnOwnThreadAsync(() => MixBulkLoop.BulkLoopAsync(context, counters, desktopIndex, startTicks, deadlineTicks, token)),
            Dedicated.RunOnOwnThreadAsync(() => MixUdpLoop.UdpLoopAsync(context, counters, tracker, observationEnds, desktopIndex, startTicks, deadlineTicks, windowTicks, token)),
        };

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
