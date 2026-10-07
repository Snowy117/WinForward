using System.Runtime.InteropServices;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Contracts.Metrics;

namespace WinForward.E2E.Client.Arms;

/// <summary>
/// Arm-wide UDP tallies folded from the per-desktop trackers, so the published class and the
/// per-desktop lanes are two views of one classification pass.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal struct UdpTotals
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

internal static class MixMetricsWriter
{
    internal static (MixMetrics Metrics, long ClientSendLoss, int IdleLanes) WriteMetrics(
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

        var metrics = new MixMetrics
        {
            Classes = new MixClassesMetrics
            {
                Page = BuildPageClass(counters),
                Bulk = BuildBulkClass(counters, elapsedTicks),
                Dns = BuildDnsClass(counters),
                Udp = BuildUdpClass(counters, udp, sentPerDesktop, windowMs),
            },
            Pages = counters._pages,
            PageConnections = counters.PageConnections,
            PageBytes = counters._pageBytes,
            BulkBytes = counters._bulkBytes,
            DnsSent = counters.DnsSent,
            UdpSent = udp._sent,
            UdpLossRate = JsonRate.Rate(udp.Loss, udp._sent),
            ClientSendLoss = udp._clientSendLoss,
            Desktops = BuildDesktopLanes(counters, trackers, counts, sentPerDesktop),
        };
        return (metrics, udp._clientSendLoss, CountIdleLanes(counters, sentPerDesktop));
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

    private static MixUdpClassMetrics BuildUdpClass(MixCounters counters, UdpTotals totals, long[] sentPerDesktop, int windowMs) => new()
    {
        Sent = totals._sent,
        Arrived = totals._arrived,
        Late = totals._late,
        Never = totals._never,
        Corrupt = totals._corrupt,
        CorruptDatagrams = totals._corruptDatagrams,
        Duplicate = totals._duplicate,
        Reordered = totals._reordered,
        UnmatchedReplies = totals._unmatched,
        ForeignConnection = totals._foreign,
        AbandonedAtTeardown = totals._undetermined,
        SendFailures = totals._sendFailures,
        WindowOverflow = totals._windowOverflow,
        OutOfRangeSequences = totals._outOfRange,
        Bytes = counters._udpBytes,
        ClientSendLoss = totals._clientSendLoss,
        LossRate = JsonRate.Rate(totals.Loss, totals._sent),
        Window = windowMs,
        SentPerDesktop = sentPerDesktop,
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

    private static MixDesktopMetrics[] BuildDesktopLanes(MixCounters counters, UdpReliabilityTracker[] trackers, LossCounts[] counts, long[] sentPerDesktop)
    {
        var lanes = new MixDesktopMetrics[trackers.Length];
        for (var desktop = 0; desktop < trackers.Length; desktop++)
        {
            lanes[desktop] = new MixDesktopMetrics
            {
                Desktop = desktop,
                UdpSent = sentPerDesktop[desktop],
                UdpArrived = counts[desktop].Arrived,
                UdpNever = counts[desktop].Never,
                UdpForeignConnection = trackers[desktop].ForeignConnection,
                PageConnections = counters.PageConnectionsPerDesktop[desktop],
                BulkFrames = counters.BulkFramesPerDesktop[desktop],
                DnsSent = counters.DnsSentPerDesktop[desktop],
            };
        }

        return lanes;
    }

    private static MixPageClassMetrics BuildPageClass(MixCounters counters) => new()
    {
        Pages = counters._pages,
        Connections = counters.PageConnections,
        Messages = counters._pageMessages,
        Bytes = counters._pageBytes,
        Errors = counters._pageErrors,
        BytesPerPage = counters._pages == 0 ? null : counters._pageBytes / counters._pages,
    };

    private static MixBulkClassMetrics BuildBulkClass(MixCounters counters, long elapsedTicks) => new()
    {
        Bytes = counters._bulkBytes,
        BytesSent = counters._bulkBytesSent,
        Frames = counters.BulkFrames,
        Errors = counters._bulkErrors,
        GoodputBps = NumberFormat.Round(counters._bulkBytes / Clock.ToSeconds(elapsedTicks)),
    };

    private static MixDnsClassMetrics BuildDnsClass(MixCounters counters) => new()
    {
        Sent = counters.DnsSent,
        Answered = counters._dnsAnswered,
        Servfail = counters._dnsServfail,
        Timeout = counters._dnsTimeout,
        Other = counters._dnsOther,
    };
}
